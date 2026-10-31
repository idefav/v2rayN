package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"io"
	"net"
	"os"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"

	"golang.org/x/crypto/ssh"
)

func TestSSHGatewayProbeWithoutPrivateExtension(t *testing.T) {
	for _, behavior := range []string{"heartbeat", "malformed", "denied", "timeout", "wrong-key", "official"} {
		t.Run(behavior, func(t *testing.T) {
			gateway := "127.0.0.1:7300"
			if behavior == "official" {
				gateway = os.Getenv("UDPGW_TEST_ADDRESS")
				if gateway == "" {
					t.Skip("set UDPGW_TEST_ADDRESS for official gateway heartbeat")
				}
				host, _, err := net.SplitHostPort(gateway)
				if err != nil || !net.ParseIP(host).IsLoopback() {
					t.Fatal("gateway must use loopback")
				}
			}
			_, key, _ := ed25519.GenerateKey(rand.Reader)
			signer, _ := ssh.NewSignerFromKey(key)
			config := &ssh.ServerConfig{NoClientAuth: true}
			config.AddHostKey(signer)
			listener, err := net.Listen("tcp", "127.0.0.1:0")
			if err != nil {
				t.Fatal(err)
			}
			defer listener.Close()
			var privateRequests atomic.Int32
			go func() {
				raw, err := listener.Accept()
				if err != nil {
					return
				}
				defer raw.Close()
				server, channels, requests, err := ssh.NewServerConn(raw, config)
				if err != nil {
					return
				}
				defer server.Close()
				go func() {
					for request := range requests {
						privateRequests.Add(1)
						request.Reply(false, nil)
					}
				}()
				for request := range channels {
					var target struct {
						Host       string
						Port       uint32
						Origin     string
						OriginPort uint32
					}
					if request.ChannelType() != "direct-tcpip" || ssh.Unmarshal(request.ExtraData(), &target) != nil || net.JoinHostPort(target.Host, strconv.Itoa(int(target.Port))) != gateway || behavior == "denied" {
						request.Reject(ssh.Prohibited, "denied")
						continue
					}
					stream, requests, err := request.Accept()
					if err != nil {
						continue
					}
					go ssh.DiscardRequests(requests)
					if behavior == "timeout" {
						continue
					}
					if behavior == "official" {
						upstream, err := net.Dial("tcp", gateway)
						if err != nil {
							stream.Close()
							continue
						}
						go func() { defer upstream.Close(); io.Copy(upstream, stream) }()
						go func() { defer stream.Close(); io.Copy(stream, upstream) }()
						continue
					}
					go func() {
						defer stream.Close()
						packet := make([]byte, 5)
						if _, err := io.ReadFull(stream, packet); err != nil {
							return
						}
						if behavior == "malformed" {
							packet[2] = 0x80
						}
						for _, b := range packet {
							stream.Write([]byte{b})
						}
					}()
				}
			}()
			if behavior == "wrong-key" {
				_, other, _ := ed25519.GenerateKey(rand.Reader)
				signer, _ = ssh.NewSignerFromKey(other)
			}
			options := &sshProbeOptions{UDPMode: "tun2proxy", UDPGateway: gateway, Server: "127.0.0.1", Port: listener.Addr().(*net.TCPAddr).Port, User: "fixture", HostKey: []string{string(ssh.MarshalAuthorizedKey(signer.PublicKey()))}}
			result := run(request{Version: 1, Kind: "ssh-capability", SSH: options, Timeout: 1})
			if behavior == "heartbeat" || behavior == "official" {
				if result.State != "passed" || result.Protocol != "SSH tun2proxy UDPGW" || !strings.Contains(result.Evidence, "heartbeat") {
					t.Fatal(result)
				}
			} else if result.State == "passed" {
				t.Fatal("accepted invalid gateway", result)
			}
			if result.Egress != "" || privateRequests.Load() != 0 {
				t.Fatal("gateway check must not use private capability or claim UDP egress", result)
			}
		})
	}
}
