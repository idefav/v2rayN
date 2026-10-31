//go:build linux

package gatewaydeploy

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func (m *Manager) install(ctx context.Context, r Request) (*Transaction, error) {
	if !identifier.MatchString(r.TransactionID) || !digestPattern.MatchString(r.BinarySHA256) {
		return nil, errors.New("E_REQUEST")
	}
	txdir, _ := m.transactionPath(r.TransactionID)
	if _, e := os.Stat(txdir); !os.IsNotExist(e) {
		return m.loadTransaction(r.TransactionID)
	}
	p, e := m.inspect(ctx, r)
	if e != nil {
		return nil, e
	}
	if p.Token != r.PlanToken {
		return nil, errors.New("E_PREFLIGHT_CHANGED")
	}
	if p.SSH.NeedsBypass && !r.EnableBypass {
		return nil, errors.New("E_BYPASS_APPROVAL_REQUIRED")
	}
	helper, e := os.Executable()
	if e != nil {
		return nil, errors.New("E_HELPER_PATH")
	}
	bin := filepath.Join(filepath.Dir(helper), "udpgw-server")
	if e = safeRegular(bin); e != nil {
		return nil, errors.New("E_UPLOAD_UNSAFE")
	}
	sha, e := fileHash(bin)
	if e != nil || sha != r.BinarySHA256 {
		return nil, errors.New("E_BINARY_CHECKSUM")
	}
	output, e := m.run(ctx, bin, "--version")
	if e != nil || !strings.Contains(string(output), Version) {
		return nil, errors.New("E_BINARY_INCOMPATIBLE")
	}
	if e = os.Mkdir(txdir, 0700); e != nil {
		return nil, e
	}
	id := randomID()
	if p.Existing != nil {
		id = p.Existing.InstallationID
	}
	t := &Transaction{ID: r.TransactionID, State: "prepared", Plan: *p, Deadline: time.Now().UTC().Add(180 * time.Second),
		Installation:    Installation{InstallationID: id, Version: Version, Architecture: p.Architecture, BinarySHA256: r.BinarySHA256, Port: p.Port, Service: Service, InstalledAt: time.Now().UTC()},
		PreviousEnabled: m.enabled(ctx), PreviousActive: m.active(ctx)}
	configInfo, e := os.Stat(p.SSH.Config)
	if e != nil {
		return nil, e
	}
	t.PreviousConfigMode = uint32(configInfo.Mode().Perm())
	if p.Existing != nil {
		t.PreviousCurrent, e = os.Readlink(filepath.Join(m.root, "current"))
		if e != nil {
			return nil, e
		}
	}
	// Backups are private root files, never included in JSON or UI logs.
	for _, entry := range [][2]string{{p.SSH.Config, "sshd_config.before"}, {m.unit, "unit.before"}, {filepath.Join(m.root, "installation.json"), "installation.before"}} {
		b, err := os.ReadFile(entry[0])
		if os.IsNotExist(err) {
			continue
		}
		if err != nil {
			return nil, err
		}
		if err = atomicWrite(filepath.Join(txdir, entry[1]), b, 0600); err != nil {
			return nil, err
		}
	}
	h, e := os.ReadFile(helper)
	if e != nil {
		return nil, e
	}
	if e = atomicWrite(filepath.Join(txdir, "helper"), h, 0700); e != nil {
		return nil, e
	}
	if e = m.saveTransaction(t); e != nil {
		return nil, e
	}
	// A durable watchdog is armed before the first service/config mutation.
	e = m.armWatchdog(ctx, t)
	if e != nil {
		t.State = "failed"
		t.Reason = "E_ROLLBACK_TIMER"
		_ = m.saveTransaction(t)
		return t, errors.New(t.Reason)
	}
	reusable := p.Existing != nil && p.Existing.Version == Version && p.Existing.BinarySHA256 == r.BinarySHA256 &&
		!p.SSH.NeedsBypass && m.active(ctx) && m.listenerOwned(ctx, *p.Existing)
	if !reusable {
		e = m.apply(ctx, r, t, bin)
	}
	if e != nil {
		t.Reason = e.Error()
		_ = m.saveTransaction(t)
		// The watchdog independently retries if local rollback cannot finish.
		_, _ = m.rollback(context.WithoutCancel(ctx), t.ID)
		fresh, _ := m.loadTransaction(t.ID)
		if fresh != nil {
			t = fresh
		}
		return t, e
	}
	t.State = "awaiting_verification"
	if e = m.saveTransaction(t); e != nil {
		return t, e
	}
	return t, nil
}

func watchdog(id string) string { return "v2rayn-udpgw-rollback-" + id }

func (m *Manager) armWatchdog(ctx context.Context, t *Transaction) error {
	dir := filepath.Dir(m.unit)
	txdir, _ := m.transactionPath(t.ID)
	service := "# v2rayN UDPGW rollback " + t.ID + "\n[Unit]\nDescription=v2rayN UDPGW transaction rollback\n[Service]\nType=oneshot\nExecStart=" + filepath.Join(txdir, "helper") + " --rollback " + t.ID + "\n"
	// Persistent calendar timer also recovers an uncommitted transaction when
	// the server reboots before the deadline. A transient timer would be lost.
	timer := "# v2rayN UDPGW rollback " + t.ID + "\n[Unit]\nDescription=v2rayN UDPGW rollback deadline\n[Timer]\nOnCalendar=" + t.Deadline.UTC().Format("2006-01-02 15:04:05 UTC") + "\nPersistent=true\nAccuracySec=1s\n[Install]\nWantedBy=timers.target\n"
	for suffix, data := range map[string]string{".service": service, ".timer": timer} {
		path := filepath.Join(dir, watchdog(t.ID)+suffix)
		if _, e := os.Lstat(path); !os.IsNotExist(e) {
			return errors.New("E_ROLLBACK_TIMER_EXISTS")
		}
		if e := atomicWrite(path, []byte(data), 0644); e != nil {
			return e
		}
	}
	if _, e := m.systemctl(ctx, "daemon-reload"); e != nil {
		return e
	}
	_, e := m.systemctl(ctx, "enable", "--now", watchdog(t.ID)+".timer")
	return e
}

func (m *Manager) stopWatchdog(ctx context.Context, id string) {
	_, _ = m.systemctl(ctx, "disable", "--now", watchdog(id)+".timer")
	// Retain the small disabled unit and root-owned helper as transaction
	// evidence; no unverified path is removed during recovery.
}

func (m *Manager) apply(ctx context.Context, r Request, t *Transaction, bin string) error {
	var e error
	reuseGateway := t.Plan.Existing != nil && t.Plan.Existing.BinarySHA256 == t.Installation.BinarySHA256 &&
		t.Plan.Existing.Version == Version && m.active(ctx) && m.listenerOwned(ctx, *t.Plan.Existing)
	if !reuseGateway {
		versionDir := filepath.Join(m.root, "versions", t.ID)
		if e = os.Mkdir(versionDir, 0755); e != nil {
			return e
		}
		if e = os.Chmod(versionDir, 0755); e != nil {
			return e
		}
		b, err := os.ReadFile(bin)
		if err != nil {
			return err
		}
		if e = atomicWrite(filepath.Join(versionDir, "udpgw-server"), b, 0755); e != nil {
			return e
		}
	}
	unit := []byte(m.serviceUnit(t.Installation))
	if t.Plan.SSH.NeedsBypass {
		before, e := os.ReadFile(t.Plan.SSH.Config)
		if e != nil || hash(before) != t.Plan.SSH.ConfigSHA256 {
			return errors.New("E_SSHD_CONFIG_CHANGED")
		}
		after, e := bypassConfig(before, r.Username, t.Installation.InstallationID, t.Plan.SSH.BypassEndpoints, t.Installation.Port)
		if e != nil {
			return e
		}
		t.ConfigAppliedSHA256 = hash(after)
		if e = m.saveTransaction(t); e != nil {
			return e
		}
		st, e := os.Stat(t.Plan.SSH.Config)
		if e != nil {
			return e
		}
		if e = atomicWrite(t.Plan.SSH.Config, after, st.Mode().Perm()); e != nil {
			return e
		}
		args := append(append([]string{}, t.Plan.SSH.Args...), "-t")
		if _, e = m.run(ctx, t.Plan.SSH.Binary, args...); e != nil {
			return errors.New("E_SSHD_CONFIG_TEST")
		}
		policy, e := m.effectiveSSH(ctx, r, t.Plan.SSH)
		if e != nil {
			return e
		}
		endpoint := net.JoinHostPort("127.0.0.1", strconv.Itoa(t.Installation.Port))
		found := false
		for _, v := range strings.Fields(policy["forwardproxybypass"]) {
			if v == endpoint {
				found = true
			}
		}
		if !found {
			return errors.New("E_SSHD_MATCH_PRECEDENCE")
		}
		if _, e = m.systemctl(ctx, "reload", t.Plan.SSH.Service); e != nil {
			return errors.New("E_SSHD_RELOAD")
		}
	}
	if reuseGateway {
		return nil
	}
	t.UnitAppliedSHA256 = hash(unit)
	if e = m.saveTransaction(t); e != nil {
		return e
	}
	if e = m.switchCurrent("versions/" + t.ID); e != nil {
		return e
	}
	if e = atomicWrite(m.unit, unit, 0644); e != nil {
		return e
	}
	if e = writeJSON(filepath.Join(m.root, "installation.json"), t.Installation); e != nil {
		return e
	}
	for _, args := range [][]string{{"daemon-reload"}, {"enable", Service}, {"restart", Service}} {
		if _, e = m.systemctl(ctx, args...); e != nil {
			return errors.New("E_SERVICE_START")
		}
	}
	// A bind race or a different listener is a verification failure, never a
	// reason to terminate a process. Client must inspect again before retrying.
	for attempt := 0; attempt < 30; attempt++ {
		if m.active(ctx) && m.listenerOwned(ctx, t.Installation) {
			return nil
		}
		select {
		case <-ctx.Done():
			return errors.New("E_SERVICE_START_CANCELLED")
		case <-time.After(100 * time.Millisecond):
		}
	}
	return errors.New("E_SERVICE_LISTENER")
}

func (m *Manager) serviceUnit(i Installation) string {
	return fmt.Sprintf(`# v2rayN UDPGW %s
[Unit]
Description=v2rayN tun2proxy UDP gateway
After=network.target
[Service]
Type=exec
DynamicUser=yes
ExecStart=%s/current/udpgw-server --listen-addr 127.0.0.1:%d --udp-mtu 65507 --udp-timeout 60
Restart=on-failure
RestartSec=2
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_INET AF_INET6
CapabilityBoundingSet=
UMask=0077
[Install]
WantedBy=multi-user.target
`, i.InstallationID, m.root, i.Port)
}

func (m *Manager) switchCurrent(target string) error {
	if !validVersionPath(target) {
		return errors.New("E_VERSION_PATH")
	}
	tmp := filepath.Join(m.root, "current-"+randomID())
	defer os.Remove(tmp)
	if e := os.Symlink(target, tmp); e != nil {
		return e
	}
	return os.Rename(tmp, filepath.Join(m.root, "current"))
}

func bypassConfig(before []byte, user, id string, endpoints []string, port int) ([]byte, error) {
	if !usernamePattern.MatchString(user) || !identifier.MatchString(id) {
		return nil, errors.New("E_BYPASS_INPUT")
	}
	begin := "# BEGIN v2rayN UDPGW " + id + " " + user + "\n"
	end := "# END v2rayN UDPGW " + id + " " + user + "\n"
	text := string(before)
	if start := strings.Index(text, begin); start >= 0 {
		stop := strings.Index(text[start:], end)
		if stop < 0 {
			return nil, errors.New("E_BYPASS_BLOCK")
		}
		text = text[:start] + text[start+stop+len(end):]
	}
	endpoint := net.JoinHostPort("127.0.0.1", strconv.Itoa(port))
	found := false
	for _, p := range endpoints {
		if p == endpoint {
			found = true
		}
	}
	if !found {
		endpoints = append(append([]string{}, endpoints...), endpoint)
	}
	if len(endpoints) > 16 {
		return nil, errors.New("E_BYPASS_LIMIT")
	}
	return []byte(strings.TrimRight(text, "\n") + "\n" + begin + "Match User " + user + "\n    ForwardProxyBypass " + strings.Join(endpoints, " ") + "\n" + end), nil
}

func (m *Manager) listenerOwned(ctx context.Context, i Installation) bool {
	pidRaw, e := m.systemctl(ctx, "show", "--property=MainPID", "--value", Service)
	pid, pe := strconv.Atoi(strings.TrimSpace(string(pidRaw)))
	if e != nil || pe != nil || pid < 2 {
		return false
	}
	// ss reports listening TCP sockets with their owning PID; check both the
	// exact loopback endpoint and the managed service process.
	b, e := m.run(ctx, "ss", "-H", "-ltnp", "sport = :"+strconv.Itoa(i.Port))
	if e != nil {
		return false
	}
	endpoint := "127.0.0.1:" + strconv.Itoa(i.Port)
	for _, line := range splitLines(b) {
		f := strings.Fields(line)
		if len(f) >= 5 && f[3] == endpoint && strings.Contains(line, "pid="+strconv.Itoa(pid)+",") {
			return true
		}
	}
	return false
}
