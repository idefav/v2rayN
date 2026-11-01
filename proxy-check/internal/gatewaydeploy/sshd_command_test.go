//go:build linux

package gatewaydeploy

import (
	"context"
	"reflect"
	"testing"
)

func TestProxySSHCommandPreservesDaemonOptions(t *testing.T) {
	const binary = "/opt/openssh-udp-20261101-97e20a4c0/sbin/sshd"
	for _, options := range []string{
		"-oAllowUdpForwarding=yes -oPidFile=/run/sshd-udp-10026/sshd.pid",
		"-o AllowUdpForwarding=yes -o PidFile=/run/sshd-udp-10026/sshd.pid",
	} {
		raw := "{ path=" + binary + " ; argv[]=" + binary + " -D -e -f /etc/ssh/sshd_config -p 10026 " + options + " ; ignore_errors=no ; }"
		args, config, err := parseSSHDCommand(raw, binary)
		want := []string{"-f", "/etc/ssh/sshd_config", "-p", "10026", "-o", "AllowUdpForwarding=yes", "-o", "PidFile=/run/sshd-udp-10026/sshd.pid"}
		if err != nil || config != "/etc/ssh/sshd_config" || !reflect.DeepEqual(args, want) {
			t.Fatalf("proxy SSH startup was not preserved: %v %q %v", args, config, err)
		}
		called := false
		m := &Manager{run: func(_ context.Context, name string, actual ...string) ([]byte, error) {
			called = true
			expected := append(append([]string{}, want...), "-T", "-C", "user=root,host=106.14.159.136,addr=106.14.159.136,laddr=172.27.95.66,lport=10026")
			if name != binary || !reflect.DeepEqual(actual, expected) {
				t.Fatalf("policy query changed the proxy listener: %s %v", name, actual)
			}
			return []byte("allowtcpforwarding yes\nallowudpforwarding yes\npermitopen any\n"), nil
		}}
		_, err = m.effectiveSSH(context.Background(), Request{Username: "root", SSHConnection: "106.14.159.136 33696 172.27.95.66 10026"}, SSHService{Binary: binary, Args: args})
		if err != nil || !called {
			t.Fatalf("proxy policy was not checked: %v", err)
		}
	}
}

func TestProxySSHCommandRejectsUnsupportedOverrides(t *testing.T) {
	for _, options := range []string{
		"-o", "-oAllowUdpForwarding", "-oAllowUdpForwarding=on",
		"-oPidFile=", "-oPidFile=relative/path", "-oPidFile=/run/a;touch",
		"-oInclude=/tmp/other.conf", "-oForwardProxyBypass=127.0.0.1:7300",
		"-o ForwardProxy=http://secret", "-oAllowTcpForwarding=no",
	} {
		raw := "{ argv[]=/usr/sbin/sshd -D " + options + " ; }"
		if _, _, err := parseSSHDCommand(raw, "/usr/sbin/sshd"); err == nil {
			t.Fatalf("accepted unsupported or ambiguous options: %s", options)
		}
	}
}

func TestProxySSHCommandPreservesDisabledUDPAndNoPidFile(t *testing.T) {
	raw := "{ argv[]=/usr/sbin/sshd -D -oallowudpforwarding=no -oPidFile=none ; }"
	args, _, err := parseSSHDCommand(raw, "/usr/sbin/sshd")
	want := []string{"-o", "allowudpforwarding=no", "-o", "PidFile=none"}
	if err != nil || !reflect.DeepEqual(args, want) {
		t.Fatalf("changed an explicit disabled policy: %v %v", args, err)
	}
}

func TestProxySSHCommandExpandsListenerEnvironment(t *testing.T) {
	raw, err := expandSSHDOptions("{ argv[]=/usr/sbin/sshd -D $SSHD_OPTS ; }", []byte("PATH=/usr/sbin\x00SSHD_OPTS=-p 10026 -oAllowUdpForwarding=yes -oPidFile=/run/proxy.pid\x00"))
	if err != nil {
		t.Fatal(err)
	}
	args, _, err := parseSSHDCommand(raw, "/usr/sbin/sshd")
	want := []string{"-p", "10026", "-o", "AllowUdpForwarding=yes", "-o", "PidFile=/run/proxy.pid"}
	if err != nil || !reflect.DeepEqual(args, want) {
		t.Fatalf("lost listener environment options: %v %v", args, err)
	}
}
