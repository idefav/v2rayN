package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strconv"
	"strings"
	"time"

	"golang.org/x/crypto/ssh"
)

type sshProbeOptions struct {
	verifiedFingerprint string
	UDPMode             string   `json:"udp_mode"`
	UDPGateway          string   `json:"udp_gateway"`
	Server              string   `json:"server"`
	Port                int      `json:"server_port"`
	User                string   `json:"user"`
	Password            string   `json:"password"`
	PrivateKey          []string `json:"private_key"`
	PrivateKeyPath      string   `json:"private_key_path"`
	Passphrase          string   `json:"private_key_passphrase"`
	HostKey             []string `json:"host_key"`
	HostKeyAlgorithms   []string `json:"host_key_algorithms"`
}

func withSSH(ctx context.Context, options *sshProbeOptions, probe func(*ssh.Client) error) error {
	if options == nil || len(options.HostKey) == 0 {
		return errors.New("E_SSH_HOST_KEY_REQUIRED")
	}
	var keys []ssh.PublicKey
	for _, text := range options.HostKey {
		key, _, _, _, err := ssh.ParseAuthorizedKey([]byte(text))
		if err != nil {
			return errors.New("E_SSH_HOST_KEY_INVALID")
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
				return errors.New("E_SSH_PRIVATE_KEY_READ")
			}
		}
		var signer ssh.Signer
		if options.Passphrase != "" {
			signer, err = ssh.ParsePrivateKeyWithPassphrase(data, []byte(options.Passphrase))
		} else {
			signer, err = ssh.ParsePrivateKey(data)
		}
		if err != nil {
			return errors.New("E_SSH_PRIVATE_KEY_PARSE")
		}
		auth = ssh.PublicKeys(signer)
	} else {
		auth = ssh.Password(options.Password)
	}
	raw, err := (&net.Dialer{}).DialContext(ctx, "tcp", net.JoinHostPort(options.Server, strconv.Itoa(options.Port)))
	if err != nil {
		if ctx.Err() != nil {
			return errors.New("E_SSH_TIMEOUT")
		}
		return errors.New("E_SSH_UNREACHABLE")
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
	keyMismatch := false
	conn, chans, requests, err := ssh.NewClientConn(raw, "configured SSH endpoint", &ssh.ClientConfig{User: user, Auth: []ssh.AuthMethod{auth}, HostKeyAlgorithms: options.HostKeyAlgorithms, HostKeyCallback: func(_ string, _ net.Addr, key ssh.PublicKey) error {
		for _, p := range keys {
			if bytes.Equal(p.Marshal(), key.Marshal()) {
				options.verifiedFingerprint = ssh.FingerprintSHA256(key)
				return nil
			}
		}
		keyMismatch = true
		return errors.New("E_SSH_HOST_KEY_MISMATCH")
	}})
	if err != nil {
		if keyMismatch {
			return errors.New("E_SSH_HOST_KEY_MISMATCH")
		}
		if ctx.Err() != nil || (!deadline.IsZero() && !time.Now().Before(deadline)) {
			return errors.New("E_SSH_TIMEOUT")
		}
		return errors.New("E_SSH_AUTH_OR_HANDSHAKE")
	}
	client := ssh.NewClient(conn, chans, requests)
	defer client.Close()
	return probe(client)
}

func probeSSH(ctx context.Context, options *sshProbeOptions, out *result) error {
	return withSSH(ctx, options, func(client *ssh.Client) error {
		if options.UDPMode == "tun2proxy" {
			if err := probeGateway(client, options.UDPGateway); err != nil {
				return err
			}
			out.Protocol = "SSH tun2proxy UDPGW"
			out.Evidence = "SSH authentication and pinned host key verified; tun2proxy gateway heartbeat matched; UDP data and egress are checked separately"
			return nil
		}
		if options.UDPMode != "" && options.UDPMode != "custom" {
			return errors.New("invalid SSH UDP mode")
		}
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
	})
}

func probeGateway(client *ssh.Client, gateway string) error {
	if gateway == "" {
		gateway = "127.0.0.1:7300"
	}
	stream, err := client.Dial("tcp", gateway)
	if err != nil {
		return errors.New("E_UDPGW_CONNECT: check TCP forwarding, gateway address and ForwardProxy")
	}
	defer stream.Close()
	request := []byte{0, 3, 1, 0, 1}
	if _, err = stream.Write(request); err != nil {
		return errors.New("E_UDPGW_PROTOCOL: gateway heartbeat write failed")
	}
	response := make([]byte, len(request))
	if _, err = io.ReadFull(stream, response); err != nil || !bytes.Equal(request, response) {
		return errors.New("E_UDPGW_PROTOCOL: gateway did not reply with a tun2proxy heartbeat")
	}
	return nil
}
