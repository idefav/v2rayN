//go:build linux

// Package gatewaydeploy implements the bounded, root-owned Linux deployment protocol.
// It never accepts arbitrary commands, unit names, installation paths or URLs.
package gatewaydeploy

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const Version = "0.8.7"
const Service = "v2rayn-udpgw.service"
const Root = "/opt/v2rayn/udpgw"
const UnitPath = "/etc/systemd/system/" + Service

var identifier = regexp.MustCompile(`^[a-f0-9]{32}$`)
var digestPattern = regexp.MustCompile(`^[a-f0-9]{64}$`)
var usernamePattern = regexp.MustCompile(`^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}\$?$`)

type Request struct {
	Version       int    `json:"version"`
	Action        string `json:"action"`
	TransactionID string `json:"transactionId"`
	PlanToken     string `json:"planToken"`
	BinarySHA256  string `json:"binarySha256"`
	Username      string `json:"username"`
	SSHConnection string `json:"sshConnection"`
	EnableBypass  bool   `json:"enableBypass"`
}

type Installation struct {
	InstallationID string    `json:"installationId"`
	Version        string    `json:"version"`
	Architecture   string    `json:"architecture"`
	BinarySHA256   string    `json:"binarySha256"`
	Port           int       `json:"port"`
	Service        string    `json:"service"`
	InstalledAt    time.Time `json:"installedAt"`
}

type SSHService struct {
	Binary          string   `json:"binary"`
	Config          string   `json:"config"`
	Service         string   `json:"service"`
	Args            []string `json:"args"`
	ConfigSHA256    string   `json:"configSha256"`
	ForwardProxy    bool     `json:"forwardProxy"`
	BypassSupported bool     `json:"bypassSupported"`
	BypassEndpoints []string `json:"bypassEndpoints"`
	NeedsBypass     bool     `json:"needsBypass"`
	PermitOpen      []string `json:"permitOpen"`
}

type Plan struct {
	Token        string        `json:"token"`
	Architecture string        `json:"architecture"`
	Port         int           `json:"port"`
	Existing     *Installation `json:"existing,omitempty"`
	SSH          SSHService    `json:"ssh"`
}

type Transaction struct {
	ID                  string       `json:"id"`
	State               string       `json:"state"`
	Plan                Plan         `json:"plan"`
	Installation        Installation `json:"installation"`
	PreviousCurrent     string       `json:"previousCurrent"`
	PreviousEnabled     bool         `json:"previousEnabled"`
	PreviousActive      bool         `json:"previousActive"`
	ConfigAppliedSHA256 string       `json:"configAppliedSha256,omitempty"`
	PreviousConfigMode  uint32       `json:"previousConfigMode,omitempty"`
	UnitAppliedSHA256   string       `json:"unitAppliedSha256,omitempty"`
	Deadline            time.Time    `json:"deadline"`
	Reason              string       `json:"reason,omitempty"`
}

type Response struct {
	Version     int          `json:"version"`
	State       string       `json:"state"`
	Error       string       `json:"error,omitempty"`
	Plan        *Plan        `json:"plan,omitempty"`
	Transaction *Transaction `json:"transaction,omitempty"`
	EchoTarget  string       `json:"echoTarget,omitempty"`
	EchoToken   string       `json:"echoToken,omitempty"`
}

type commandRunner func(context.Context, string, ...string) ([]byte, error)

type Manager struct {
	// root and unit are fixed in production; private injection permits isolated
	// failure/rollback tests without changing host systemd or sshd.
	root, unit string
	run        commandRunner
}

func New() *Manager { return &Manager{root: Root, unit: UnitPath, run: runCommand} }

func runCommand(ctx context.Context, name string, args ...string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	cmd.Env = []string{"PATH=/usr/sbin:/usr/bin:/sbin:/bin", "LANG=C", "LC_ALL=C"}
	// Avoid unbounded output and never return command output in public errors:
	// sshd -T may contain a configured upstream password.
	b := &limitedBuffer{remaining: 256 * 1024}
	cmd.Stdout, cmd.Stderr = b, io.Discard
	err := cmd.Run()
	return b.data, err
}

type limitedBuffer struct {
	data      []byte
	remaining int
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	if len(p) > b.remaining {
		return 0, errors.New("output limit")
	}
	b.data = append(b.data, p...)
	b.remaining -= len(p)
	return len(p), nil
}

func hash(data []byte) string { h := sha256.Sum256(data); return hex.EncodeToString(h[:]) }
func fileHash(path string) (string, error) {
	b, e := os.ReadFile(path)
	if e != nil {
		return "", e
	}
	return hash(b), nil
}
func randomID() string {
	var b [16]byte
	if _, e := rand.Read(b[:]); e != nil {
		panic(e)
	}
	return hex.EncodeToString(b[:])
}

func atomicWrite(path string, data []byte, mode os.FileMode) error {
	f, err := os.OpenFile(path+".tmp-"+randomID(), os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode)
	if err != nil {
		return err
	}
	name := f.Name()
	defer os.Remove(name)
	if err = f.Chmod(mode); err != nil {
		f.Close()
		return err
	}
	if _, err = f.Write(data); err == nil {
		err = f.Sync()
	}
	ce := f.Close()
	if err == nil {
		err = ce
	}
	if err != nil {
		return err
	}
	if err = os.Rename(name, path); err != nil {
		return err
	}
	d, err := os.Open(filepath.Dir(path))
	if err != nil {
		return err
	}
	defer d.Close()
	return d.Sync()
}

func writeJSON(path string, v any) error {
	b, e := json.MarshalIndent(v, "", "  ")
	if e != nil {
		return e
	}
	return atomicWrite(path, append(b, '\n'), 0600)
}
func readJSON(path string, v any) error {
	b, e := os.ReadFile(path)
	if e != nil {
		return e
	}
	if len(b) > 65536 {
		return errors.New("record too large")
	}
	return json.Unmarshal(b, v)
}

func (m *Manager) transactionPath(id string) (string, error) {
	if !identifier.MatchString(id) {
		return "", errors.New("E_TRANSACTION_ID")
	}
	return filepath.Join(m.root, "transactions", id), nil
}
func (m *Manager) loadTransaction(id string) (*Transaction, error) {
	p, e := m.transactionPath(id)
	if e != nil {
		return nil, e
	}
	var t Transaction
	if e = readJSON(filepath.Join(p, "record.json"), &t); e != nil {
		return nil, errors.New("E_TRANSACTION_UNKNOWN")
	}
	if t.ID != id {
		return nil, errors.New("E_TRANSACTION_ID")
	}
	return &t, nil
}
func (m *Manager) saveTransaction(t *Transaction) error {
	p, e := m.transactionPath(t.ID)
	if e != nil {
		return e
	}
	return writeJSON(filepath.Join(p, "record.json"), t)
}
func (m *Manager) systemctl(ctx context.Context, args ...string) ([]byte, error) {
	return m.run(ctx, "systemctl", args...)
}
func (m *Manager) active(ctx context.Context) bool {
	_, e := m.systemctl(ctx, "is-active", "--quiet", Service)
	return e == nil
}
func (m *Manager) enabled(ctx context.Context) bool {
	_, e := m.systemctl(ctx, "is-enabled", "--quiet", Service)
	return e == nil
}
func fail(code string, err error) error {
	if err == nil {
		return nil
	}
	return fmt.Errorf("%s", code)
}
func splitLines(data []byte) []string { return strings.Split(strings.TrimSpace(string(data)), "\n") }
