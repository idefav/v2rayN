package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"testing"

	"golang.org/x/crypto/ssh"
)

func TestSSHCapabilityClassification(t *testing.T) {
	for _, tc := range []struct {
		name   string
		status int
		want   string
	}{
		{"original", -1, "unsupported"}, {"enabled", 0, "enabled"}, {"disabled", 1, "disabled"},
		{"upstream", 2, "upstream_unsupported"}, {"malformed", 9, "unknown"}, {"authentication", -2, "unknown"}, {"timeout", -3, "unknown"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			_, key, _ := ed25519.GenerateKey(rand.Reader)
			signer, _ := ssh.NewSignerFromKey(key)
			config := &ssh.ServerConfig{PasswordCallback: func(_ ssh.ConnMetadata, _ []byte) (*ssh.Permissions, error) {
				if tc.status == -2 {
					return nil, errors.New("rejected")
				}
				return nil, nil
			}}
			config.AddHostKey(signer)
			l, e := net.Listen("tcp", "127.0.0.1:0")
			if e != nil {
				t.Fatal(e)
			}
			defer l.Close()
			done := make(chan struct{})
			defer func() { <-done }()
			go func() {
				defer close(done)
				raw, e := l.Accept()
				if e != nil {
					return
				}
				defer raw.Close()
				server, chs, reqs, e := ssh.NewServerConn(raw, config)
				if e != nil {
					return
				}
				defer server.Close()
				go func() {
					for ch := range chs {
						stream, req, e := ch.Accept()
						if e != nil {
							continue
						}
						go ssh.DiscardRequests(req)
						go func() {
							defer stream.Close()
							var b [5]byte
							if _, e := io.ReadFull(stream, b[:]); e == nil {
								stream.Write(b[:])
							}
						}()
					}
				}()
				for r := range reqs {
					switch r.Type {
					case "udp-forwarding-v1@idefav.github.io":
						if tc.status == -3 {
							continue
						}
						r.Reply(tc.status >= 0, []byte{byte(tc.status)})
					case "forward-proxy-info-v1@idefav.github.io":
						r.Reply(true, ssh.Marshal(struct {
							Version  uint32
							Enabled  bool
							Count    uint32
							Endpoint string
						}{1, true, 1, "127.0.0.1:7300"}))
					default:
						r.Reply(false, nil)
					}
				}
			}()
			r := run(request{Version: 1, Kind: "ssh-udp-capabilities", Timeout: 1, SSH: &sshProbeOptions{Server: "127.0.0.1", Port: l.Addr().(*net.TCPAddr).Port, User: "fixture", Password: "test", HostKey: []string{string(ssh.MarshalAuthorizedKey(signer.PublicKey()))}}})
			if r.SSHCapabilities == nil || r.SSHCapabilities.Custom != tc.want {
				t.Fatalf("%+v: %+v", r, r.SSHCapabilities)
			}
			if tc.want != "unknown" && (r.SSHCapabilities.Gateway != "ready" || !r.SSHCapabilities.PolicyKnown || r.SSHCapabilities.HostFingerprint == "") {
				t.Fatalf("independent observations missing: %+v", r.SSHCapabilities)
			}
			if tc.want == "unknown" && r.State == "unsupported" {
				t.Fatal("failure was classified as unsupported")
			}
		})
	}
}

func TestPolicyDecoderRejectsNonLoopbackAndMalformedPayloads(t *testing.T) {
	valid := ssh.Marshal(struct {
		Version  uint32
		Enabled  bool
		Count    uint32
		Endpoint string
	}{1, true, 1, "[::1]:7300"})
	if e := decodeForwardPolicy(valid, &sshUDPCapabilities{}); e != nil {
		t.Fatal(e)
	}
	public := ssh.Marshal(struct {
		Version  uint32
		Enabled  bool
		Count    uint32
		Endpoint string
	}{1, true, 1, "8.8.8.8:7300"})
	many := append([]byte{}, valid...)
	binary.BigEndian.PutUint32(many[5:9], 17)
	for _, bad := range [][]byte{nil, valid[:len(valid)-1], append(append([]byte{}, valid...), 0, 0), append(append([]byte{}, valid...), 5), public, many} {
		if decodeForwardPolicy(bad, &sshUDPCapabilities{}) == nil {
			t.Fatal("accepted malformed policy")
		}
	}
}

func TestPolicyRefinesDisabledReasonWithoutChangingUDPv1Status(t *testing.T) {
	for detail, want := range map[byte]string{1: "E_UDP_ALLOW_DISABLED", 2: "E_UDP_DISABLE_FORWARDING", 3: "E_UDP_KEY_RESTRICTED"} {
		p := ssh.Marshal(struct {
			Version uint32
			Enabled bool
			Count   uint32
		}{1, false, 0})
		c := &sshUDPCapabilities{Custom: "disabled"}
		if e := decodeForwardPolicy(append(p, detail), c); e != nil || c.Custom != "disabled" || c.Reason != want {
			t.Fatalf("%+v: %v", c, e)
		}
	}
}
