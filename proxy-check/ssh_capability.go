package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"

	"golang.org/x/crypto/ssh"
)

type sshProbeOptions struct {
	Server            string   `json:"server"`
	Port              int      `json:"server_port"`
	User              string   `json:"user"`
	Password          string   `json:"password"`
	PrivateKey        []string `json:"private_key"`
	PrivateKeyPath    string   `json:"private_key_path"`
	Passphrase        string   `json:"private_key_passphrase"`
	HostKey           []string `json:"host_key"`
	HostKeyAlgorithms []string `json:"host_key_algorithms"`
}

func probeSSH(ctx context.Context, options *sshProbeOptions, out *result) error {
	if options == nil || len(options.HostKey) == 0 {
		return errors.New("SSH capability probe requires a pinned host key")
	}
	var keys []ssh.PublicKey
	for _, text := range options.HostKey {
		key, _, _, _, err := ssh.ParseAuthorizedKey([]byte(text))
		if err != nil {
			return errors.New("invalid pinned SSH key")
		}
		keys = append(keys, key)
	}
	var auth ssh.AuthMethod
	if options.PrivateKeyPath != "" || len(options.PrivateKey) > 0 {
		data := []byte(strings.Join(options.PrivateKey, "\n"))
		var err error
		if len(data) == 0 {
			data, err = os.ReadFile(options.PrivateKeyPath)
			if err != nil {
				return errors.New("cannot read SSH private key")
			}
		}
		var signer ssh.Signer
		if options.Passphrase != "" {
			signer, err = ssh.ParsePrivateKeyWithPassphrase(data, []byte(options.Passphrase))
		} else {
			signer, err = ssh.ParsePrivateKey(data)
		}
		if err != nil {
			return errors.New("cannot parse SSH private key")
		}
		auth = ssh.PublicKeys(signer)
	} else {
		auth = ssh.Password(options.Password)
	}
	raw, err := (&net.Dialer{}).DialContext(ctx, "tcp", net.JoinHostPort(options.Server, strconv.Itoa(options.Port)))
	if err != nil {
		return errors.New("SSH endpoint unreachable")
	}
	defer raw.Close()
	stop := context.AfterFunc(ctx, func() { raw.Close() })
	defer stop()
	deadline, _ := ctx.Deadline()
	raw.SetDeadline(deadline)
	user := options.User
	if user == "" {
		user = "root"
	}
	conn, chans, requests, err := ssh.NewClientConn(raw, "configured SSH endpoint", &ssh.ClientConfig{User: user, Auth: []ssh.AuthMethod{auth}, HostKeyAlgorithms: options.HostKeyAlgorithms, HostKeyCallback: func(_ string, _ net.Addr, key ssh.PublicKey) error {
		for _, p := range keys {
			if bytes.Equal(p.Marshal(), key.Marshal()) {
				return nil
			}
		}
		return errors.New("pinned host key mismatch")
	}})
	if err != nil {
		return errors.New("SSH authentication or pinned host key verification failed")
	}
	client := ssh.NewClient(conn, chans, requests)
	defer client.Close()
	out.Protocol = "SSH UDP v1"
	out.Evidence = "SSH authentication and pinned host key verified; "
	accepted, payload, err := client.SendRequest("udp-forwarding-v1@idefav.github.io", true, nil)
	if err != nil {
		return errors.New("SSH capability request failed")
	}
	if !accepted {
		return fmt.Errorf("%w: E_UDP_PROXY_UNSUPPORTED", errUnsupported)
	}
	if len(payload) != 1 {
		return errors.New("E_UDP_PROTOCOL")
	}
	switch payload[0] {
	case 0:
		out.Evidence += "remote UDP capability enabled; data path is checked separately"
		return nil
	case 1:
		return fmt.Errorf("%w: E_UDP_FORWARDING_DISABLED", errUnsupported)
	case 2:
		return fmt.Errorf("%w: E_UDP_UPSTREAM_UNSUPPORTED", errUnsupported)
	default:
		return errors.New("E_UDP_PROTOCOL")
	}
}
