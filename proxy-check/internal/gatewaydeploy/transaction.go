//go:build linux

package gatewaydeploy

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"os"
	"path/filepath"
	"syscall"
	"time"
)

func (m *Manager) Execute(ctx context.Context, r Request, stream io.Writer) (out Response) {
	out = Response{Version: 1, State: "failed"}
	if r.Version != 1 {
		out.Error = "E_VERSION"
		return
	}
	for _, dir := range []string{m.root, filepath.Join(m.root, "transactions"), filepath.Join(m.root, "versions")} {
		if e := secureDirectory(dir); e != nil {
			out.Error = "E_UNSAFE_INSTALL_PATH"
			return
		}
	}
	lock, e := os.OpenFile(filepath.Join(m.root, "lock"), os.O_CREATE|os.O_RDWR|syscall.O_NOFOLLOW, 0600)
	if e != nil {
		out.Error = "E_LOCK"
		return
	}
	defer lock.Close()
	if e = syscall.Flock(int(lock.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); e != nil {
		out.Error = "E_DEPLOY_BUSY"
		return
	}
	defer syscall.Flock(int(lock.Fd()), syscall.LOCK_UN)
	switch r.Action {
	case "inspect":
		out.Plan, e = m.inspect(ctx, r)
	case "install":
		// Only one pending transaction per host, including disconnected clients.
		entries, _ := os.ReadDir(filepath.Join(m.root, "transactions"))
		for _, entry := range entries {
			t, err := m.loadTransaction(entry.Name())
			if err == nil && t.ID != r.TransactionID && (t.State == "prepared" || t.State == "awaiting_verification" || t.State == "rollback_attention") {
				out.Error = "E_PENDING_TRANSACTION"
				return
			}
		}
		out.Transaction, e = m.install(ctx, r)
	case "status":
		out.Transaction, e = m.loadTransaction(r.TransactionID)
	case "commit":
		out.Transaction, e = m.commit(ctx, r.TransactionID)
	case "rollback":
		out.Transaction, e = m.rollback(ctx, r.TransactionID)
	case "echo":
		t, err := m.loadTransaction(r.TransactionID)
		if err != nil {
			e = err
			break
		}
		if t.State != "awaiting_verification" || time.Now().After(t.Deadline) {
			e = errors.New("E_TRANSACTION_STATE")
			break
		}
		// Echo lifetime does not block cancellation, inspection or rollback.
		_ = syscall.Flock(int(lock.Fd()), syscall.LOCK_UN)
		e = m.echo(ctx, t, stream)
	default:
		e = errors.New("E_ACTION")
	}
	if e != nil {
		out.Error = e.Error()
		return
	}
	out.State = "ok"
	return
}

func (m *Manager) echo(ctx context.Context, t *Transaction, stream io.Writer) error {
	c, e := net.ListenPacket("udp4", "127.0.0.1:0")
	if e != nil {
		return errors.New("E_ECHO_LISTEN")
	}
	defer c.Close()
	var token [32]byte
	if _, e = rand.Read(token[:]); e != nil {
		return e
	}
	deadline := time.Now().Add(30 * time.Second)
	if t.Deadline.Before(deadline) {
		deadline = t.Deadline
	}
	c.SetDeadline(deadline)
	stop := context.AfterFunc(ctx, func() { c.Close() })
	defer stop()
	if e = json.NewEncoder(stream).Encode(Response{Version: 1, State: "echo_ready", EchoTarget: c.LocalAddr().String(), EchoToken: hex.EncodeToString(token[:])}); e != nil {
		return e
	}
	var buf [64]byte
	for {
		n, addr, err := c.ReadFrom(buf[:])
		if err != nil {
			return errors.New("E_ECHO_TIMEOUT")
		}
		if !bytes.Equal(buf[:n], token[:]) {
			continue
		}
		// Receipt is durable before responding so a successful client echo is
		// sufficient to commit even if this SSH exec channel closes immediately.
		path, _ := m.transactionPath(t.ID)
		if e = atomicWrite(filepath.Join(path, "echo.ok"), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0600); e != nil {
			return e
		}
		_, e = c.WriteTo(token[:], addr)
		return e
	}
}

func (m *Manager) commit(ctx context.Context, id string) (*Transaction, error) {
	t, e := m.loadTransaction(id)
	if e != nil {
		return nil, e
	}
	if t.State == "committed" {
		return t, nil
	}
	if t.State != "awaiting_verification" || time.Now().After(t.Deadline) {
		return t, errors.New("E_TRANSACTION_STATE")
	}
	p, _ := m.transactionPath(id)
	if _, e = os.Stat(filepath.Join(p, "echo.ok")); e != nil {
		return t, errors.New("E_ECHO_REQUIRED")
	}
	if !m.active(ctx) || !m.listenerOwned(ctx, t.Installation) {
		return t, errors.New("E_SERVICE_LISTENER")
	}
	// Commit the durable record before stopping the watchdog. A concurrently
	// launched watchdog takes this same lock and observes the committed state.
	t.State = "committed"
	if e = m.saveTransaction(t); e != nil {
		return t, e
	}
	m.stopWatchdog(ctx, id)
	return t, nil
}

func restoreFile(path, backup, applied string, mode os.FileMode) error {
	before, be := os.ReadFile(backup)
	now, ne := os.ReadFile(path)
	if be != nil && !os.IsNotExist(be) {
		return be
	}
	if ne != nil && !os.IsNotExist(ne) {
		return ne
	}
	if be == nil && ne == nil && bytes.Equal(before, now) {
		return nil
	}
	if os.IsNotExist(be) && os.IsNotExist(ne) {
		return nil
	}
	if ne == nil && (applied == "" || hash(now) != applied) {
		return errors.New("E_EXTERNAL_CHANGE")
	}
	if be == nil {
		return atomicWrite(path, before, mode)
	}
	if ne == nil {
		return os.Remove(path)
	}
	return nil
}

func (m *Manager) rollback(ctx context.Context, id string) (*Transaction, error) {
	t, e := m.loadTransaction(id)
	if e != nil {
		return nil, e
	}
	if t.State == "committed" || t.State == "rolled_back" || t.State == "failed" {
		return t, nil
	}
	dir, _ := m.transactionPath(id)
	attention := func(err error) (*Transaction, error) {
		t.State = "rollback_attention"
		t.Reason = err.Error()
		_ = m.saveTransaction(t)
		return t, err
	}
	// Check for administrator changes before touching the live service.
	unit, ue := os.ReadFile(m.unit)
	before, _ := os.ReadFile(filepath.Join(dir, "unit.before"))
	if ue == nil && hash(unit) != t.UnitAppliedSHA256 && !bytes.Equal(unit, before) {
		return attention(errors.New("E_UNIT_CHANGED"))
	}
	current, ce := os.Readlink(filepath.Join(m.root, "current"))
	if ce == nil && current != "versions/"+id && current != t.PreviousCurrent {
		return attention(errors.New("E_CURRENT_CHANGED"))
	}
	if t.ConfigAppliedSHA256 != "" {
		mode := os.FileMode(t.PreviousConfigMode) & 0777
		if mode == 0 {
			mode = 0600
		} // Recover older private transaction records.
		if e = restoreFile(t.Plan.SSH.Config, filepath.Join(dir, "sshd_config.before"), t.ConfigAppliedSHA256, mode); e != nil {
			return attention(errors.New("E_SSHD_ROLLBACK_CONFLICT"))
		}
		args := append(append([]string{}, t.Plan.SSH.Args...), "-t")
		if _, e = m.run(ctx, t.Plan.SSH.Binary, args...); e != nil {
			return attention(errors.New("E_SSHD_ROLLBACK_TEST"))
		}
		if _, e = m.systemctl(ctx, "reload", t.Plan.SSH.Service); e != nil {
			return attention(errors.New("E_SSHD_ROLLBACK_RELOAD"))
		}
	}
	if t.UnitAppliedSHA256 != "" {
		if _, e = m.systemctl(ctx, "stop", Service); e != nil {
			return attention(errors.New("E_ROLLBACK_STOP"))
		}
		// Remove enablement while the managed unit still exists, so a failed
		// first installation leaves no dangling multi-user.target symlink.
		if !t.PreviousEnabled {
			if _, e = m.systemctl(ctx, "disable", Service); e != nil {
				return attention(errors.New("E_ROLLBACK_ENABLE"))
			}
		}
		if e = restoreFile(m.unit, filepath.Join(dir, "unit.before"), t.UnitAppliedSHA256, 0644); e != nil {
			return attention(e)
		}
		if t.PreviousCurrent != "" {
			e = m.switchCurrent(t.PreviousCurrent)
		} else {
			e = os.Remove(filepath.Join(m.root, "current"))
			if os.IsNotExist(e) {
				e = nil
			}
		}
		if e != nil {
			return attention(e)
		}
		if t.Plan.Existing != nil {
			e = writeJSON(filepath.Join(m.root, "installation.json"), t.Plan.Existing)
		} else {
			e = os.Remove(filepath.Join(m.root, "installation.json"))
			if os.IsNotExist(e) {
				e = nil
			}
		}
		if e != nil {
			return attention(e)
		}
		if _, e = m.systemctl(ctx, "daemon-reload"); e != nil {
			return attention(errors.New("E_ROLLBACK_RELOAD"))
		}
		if t.PreviousEnabled {
			_, e = m.systemctl(ctx, "enable", Service)
		}
		if e != nil {
			return attention(errors.New("E_ROLLBACK_ENABLE"))
		}
		if t.PreviousActive {
			if _, e = m.systemctl(ctx, "start", Service); e != nil {
				return attention(errors.New("E_ROLLBACK_START"))
			}
		}
	}
	t.State = "rolled_back"
	if e = m.saveTransaction(t); e != nil {
		return t, e
	}
	m.stopWatchdog(ctx, id)
	return t, nil
}
