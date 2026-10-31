package gatewaydeploy

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestCommandIdentityRejectsShellOrHiddenConfiguration(t *testing.T) {
	for _, raw := range []string{
		"{ path=/usr/sbin/sshd ; argv[]=/usr/sbin/sshd -D $SSHD_OPTS ; }",
		"{ argv[]=/bin/sh -c sshd ; }", "{ argv[]=/usr/sbin/sshd -o ForwardProxy=http://secret ; }",
	} {
		if _, _, e := parseSSHDCommand(raw, "/usr/sbin/sshd"); e == nil {
			t.Fatal("ambiguous command accepted", raw)
		}
	}
	args, path, e := parseSSHDCommand("{ path=/usr/sbin/sshd ; argv[]=/usr/sbin/sshd -D -f /etc/ssh/custom.conf -p 2222 ; }", "/usr/sbin/sshd")
	if e != nil || path != "/etc/ssh/custom.conf" || len(args) != 4 {
		t.Fatal(args, path, e)
	}
}

func TestBypassIsPerAccountAndIdempotent(t *testing.T) {
	id := strings.Repeat("a", 32)
	original := []byte("Port 22\nMatch User other\n PermitOpen none\n")
	first, e := bypassConfig(original, "alice", id, nil, 7300)
	if e != nil {
		t.Fatal(e)
	}
	second, e := bypassConfig(first, "alice", id, []string{"127.0.0.1:7300"}, 7300)
	if e != nil || string(first) != string(second) {
		t.Fatal("not idempotent", e)
	}
	third, e := bypassConfig(second, "bob", id, nil, 7301)
	if e != nil || !strings.Contains(string(third), "Match User alice") || !strings.Contains(string(third), "Match User bob") {
		t.Fatal("lost another account", e)
	}
	if _, e = bypassConfig(original, "bob\nPermitOpen any", id, nil, 7300); e == nil {
		t.Fatal("accepted injected username")
	}
}

func TestRollbackRestoresOnlyOwnedState(t *testing.T) {
	for _, conflict := range []bool{false, true} {
		t.Run(map[bool]string{false: "restore", true: "external-change"}[conflict], func(t *testing.T) {
			root := t.TempDir()
			id := strings.Repeat("b", 32)
			old := strings.Repeat("a", 32)
			m := &Manager{root: root, unit: filepath.Join(root, "service"), run: func(context.Context, string, ...string) ([]byte, error) { return nil, nil }}
			dir := filepath.Join(root, "transactions", id)
			if e := os.MkdirAll(dir, 0700); e != nil {
				t.Fatal(e)
			}
			before := []byte("old unit")
			after := []byte("new unit")
			os.WriteFile(filepath.Join(dir, "unit.before"), before, 0600)
			os.WriteFile(m.unit, after, 0644)
			os.Symlink("versions/"+id, filepath.Join(root, "current"))
			tx := &Transaction{ID: id, State: "awaiting_verification", UnitAppliedSHA256: hash(after), PreviousCurrent: "versions/" + old, Plan: Plan{Existing: &Installation{InstallationID: old, Port: 7300, Service: Service}}}
			if e := m.saveTransaction(tx); e != nil {
				t.Fatal(e)
			}
			if conflict {
				os.WriteFile(m.unit, []byte("administrator edit"), 0644)
			}
			result, e := m.rollback(context.Background(), id)
			if conflict {
				if e == nil || result.State != "rollback_attention" {
					t.Fatal("overwrote external change")
				}
				data, _ := os.ReadFile(m.unit)
				if string(data) != "administrator edit" {
					t.Fatal("changed unowned unit")
				}
				return
			}
			if e != nil || result.State != "rolled_back" {
				t.Fatal(result, e)
			}
			data, _ := os.ReadFile(m.unit)
			current, _ := os.Readlink(filepath.Join(root, "current"))
			if string(data) != string(before) || current != "versions/"+old {
				t.Fatal("rollback incomplete")
			}
		})
	}
}

func TestWatchdogSurvivesRebootAndCommitRequiresEcho(t *testing.T) {
	root := t.TempDir()
	id := strings.Repeat("a", 32)
	m := &Manager{root: root, unit: filepath.Join(root, Service), run: func(context.Context, string, ...string) ([]byte, error) { return nil, nil }}
	os.MkdirAll(filepath.Join(root, "transactions", id), 0700)
	tx := &Transaction{ID: id, State: "awaiting_verification", Deadline: time.Now().Add(time.Minute)}
	m.saveTransaction(tx)
	if e := m.armWatchdog(context.Background(), tx); e != nil {
		t.Fatal(e)
	}
	b, e := os.ReadFile(filepath.Join(root, watchdog(id)+".timer"))
	if e != nil || !strings.Contains(string(b), "Persistent=true") || !strings.Contains(string(b), "OnCalendar=") {
		t.Fatal("not durable", e)
	}
	if _, e := m.commit(context.Background(), id); e == nil || e.Error() != "E_ECHO_REQUIRED" {
		t.Fatal("unverified commit", e)
	}
}

func TestRestoreDoesNotOverwriteExternalSSHConfiguration(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "sshd_config")
	backup := filepath.Join(dir, "before")
	os.WriteFile(path, []byte("external"), 0600)
	os.WriteFile(backup, []byte("before"), 0600)
	if restoreFile(path, backup, hash([]byte("installed")), 0600) == nil {
		t.Fatal("overwrote changed config")
	}
}

func TestPermitOpenKeepsLiteralHostAndPortRestrictions(t *testing.T) {
	for _, tc := range []struct {
		value string
		want  bool
	}{
		{"any", true}, {"none", false}, {"127.0.0.1:7300", true}, {"localhost:7300", false},
		{"127.0.0.1:7301", false}, {"*:7300", true}, {"127.0.0.1:*", true}, {"[::1]:7300", false},
	} {
		if got := tcpPermitted(strings.Fields(tc.value), "127.0.0.1", 7300); got != tc.want {
			t.Fatalf("%s: %v", tc.value, got)
		}
	}
}

func TestRollbackPreservesSSHMode(t *testing.T) {
	root := t.TempDir()
	id := strings.Repeat("c", 32)
	dir := filepath.Join(root, "transactions", id)
	os.MkdirAll(dir, 0700)
	config := filepath.Join(root, "sshd_config")
	before := []byte("before")
	after := []byte("after")
	os.WriteFile(config, after, 0640)
	os.WriteFile(filepath.Join(dir, "sshd_config.before"), before, 0600)
	m := &Manager{root: root, unit: filepath.Join(root, "unit"), run: func(context.Context, string, ...string) ([]byte, error) { return nil, nil }}
	tx := &Transaction{ID: id, State: "awaiting_verification", ConfigAppliedSHA256: hash(after), PreviousConfigMode: 0640, Plan: Plan{SSH: SSHService{Config: config, Binary: "fixture", Service: "fixture.service"}}}
	m.saveTransaction(tx)
	if _, e := m.rollback(context.Background(), id); e != nil {
		t.Fatal(e)
	}
	st, e := os.Stat(config)
	if e != nil || st.Mode().Perm() != 0640 {
		t.Fatal("configuration mode lost", e)
	}
}
