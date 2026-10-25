package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"net"
	"testing"

	"golang.org/x/crypto/ssh"
)

func TestSSHCapabilityStatesAndPinnedKey(t *testing.T) {
	for _, status := range []int{-1, 0, 1, 2} {
		_, key, _ := ed25519.GenerateKey(rand.Reader)
		signer, _ := ssh.NewSignerFromKey(key)
		listener, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		conf := &ssh.ServerConfig{PasswordCallback: func(_ ssh.ConnMetadata, p []byte) (*ssh.Permissions, error) {
			if string(p) != "fixture-password" {
				return nil, errors.New("denied")
			}
			return nil, nil
		}}
		conf.AddHostKey(signer)
		go func() {
			for {
				raw, e := listener.Accept()
				if e != nil {
					return
				}
				go func() {
					conn, channels, requests, e := ssh.NewServerConn(raw, conf)
					if e != nil {
						raw.Close()
						return
					}
					defer conn.Close()
					go func() {
						for ch := range channels {
							ch.Reject(ssh.Prohibited, "no data path")
						}
					}()
					for req := range requests {
						if status < 0 {
							req.Reply(false, nil)
						} else {
							req.Reply(true, []byte{byte(status)})
						}
					}
				}()
			}
		}()
		addr := listener.Addr().(*net.TCPAddr)
		options := &sshProbeOptions{Server: "127.0.0.1", Port: addr.Port, User: "fixture", Password: "fixture-password", HostKey: []string{string(ssh.MarshalAuthorizedKey(signer.PublicKey()))}}
		result := run(request{Version: 1, Kind: "ssh-capability", SSH: options, Timeout: 2})
		want := "unsupported"
		if status == 0 {
			want = "passed"
		}
		if result.State != want {
			t.Fatal(status, result)
		}
		if result.Egress != "" {
			t.Fatal("capability is not an egress probe")
		}
		_, wrong, _ := ed25519.GenerateKey(rand.Reader)
		other, _ := ssh.NewSignerFromKey(wrong)
		options.HostKey = []string{string(ssh.MarshalAuthorizedKey(other.PublicKey()))}
		result = run(request{Version: 1, Kind: "ssh-capability", SSH: options, Timeout: 2})
		if result.State != "failed" {
			t.Fatal("accepted wrong pinned key", result)
		}
		listener.Close()
	}
}
