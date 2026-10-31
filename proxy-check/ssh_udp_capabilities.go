package main

import (
	"context"
	"encoding/binary"
	"errors"
	"net"
	"strconv"
	"time"

	"golang.org/x/crypto/ssh"
)

// Declaration, policy and heartbeat are separate observations, not UDP egress.
type sshUDPCapabilities struct {
	Custom              string    `json:"custom"`
	Reason              string    `json:"reason"`
	HostFingerprint     string    `json:"hostFingerprint,omitempty"`
	CheckedAt           time.Time `json:"checkedAt"`
	PolicyKnown         bool      `json:"policyKnown"`
	ForwardProxyEnabled bool      `json:"forwardProxyEnabled"`
	BypassSupported     bool      `json:"bypassSupported"`
	BypassEndpoints     []string  `json:"bypassEndpoints"`
	Gateway             string    `json:"gateway"`
	GatewayReason       string    `json:"gatewayReason,omitempty"`
}

func probeSSHCapabilities(ctx context.Context, options *sshProbeOptions, out *result) error {
	c := &sshUDPCapabilities{Custom: "unknown", Reason: "E_SSH_PROBE_FAILED", Gateway: "unknown", CheckedAt: time.Now().UTC()}
	out.SSHCapabilities = c
	err := withSSH(ctx, options, func(client *ssh.Client) error {
		accepted, payload, err := client.SendRequest("udp-forwarding-v1@idefav.github.io", true, nil)
		if err != nil {
			return errors.New("E_SSH_CAPABILITY_QUERY")
		}
		if !accepted {
			c.Custom, c.Reason = "unsupported", "E_UDP_PROXY_UNSUPPORTED"
		} else if len(payload) != 1 {
			return errors.New("E_UDP_PROTOCOL")
		} else {
			switch payload[0] {
			case 0:
				c.Custom, c.Reason = "enabled", "E_UDP_DECLARED_ENABLED"
			case 1:
				c.Custom, c.Reason = "disabled", "E_UDP_FORWARDING_DISABLED"
			case 2:
				c.Custom, c.Reason = "upstream_unsupported", "E_UDP_UPSTREAM_UNSUPPORTED"
			default:
				return errors.New("E_UDP_PROTOCOL")
			}
		}
		accepted, payload, err = client.SendRequest("forward-proxy-info-v1@idefav.github.io", true, nil)
		if err != nil {
			return errors.New("E_SSH_POLICY_QUERY")
		}
		if accepted {
			if err := decodeForwardPolicy(payload, c); err != nil {
				return err
			}
		}
		if err := probeGateway(client, options.UDPGateway); err != nil {
			c.Gateway, c.GatewayReason = "failed", err.Error()
		} else {
			c.Gateway = "ready"
		}
		return nil
	})
	if err != nil && c.Custom == "unknown" {
		c.Reason = err.Error()
		if ctx.Err() != nil {
			c.Reason = "E_SSH_TIMEOUT"
		}
	}
	if options != nil {
		c.HostFingerprint = options.verifiedFingerprint
	}
	out.Protocol = "SSH UDP capabilities v1"
	out.Evidence = "Authenticated protocol responses; declaration and gateway heartbeat only"
	return err
}

func decodeForwardPolicy(p []byte, c *sshUDPCapabilities) error {
	invalid := errors.New("E_SSH_POLICY_PROTOCOL")
	if len(p) < 9 || binary.BigEndian.Uint32(p) != 1 || p[4] > 1 {
		return invalid
	}
	enabled := p[4] == 1
	count := binary.BigEndian.Uint32(p[5:])
	p = p[9:]
	if count > 16 {
		return invalid
	}
	endpoints := make([]string, 0, count)
	for i := uint32(0); i < count; i++ {
		if len(p) < 4 {
			return invalid
		}
		n := binary.BigEndian.Uint32(p)
		p = p[4:]
		if n == 0 || n > 80 || uint32(len(p)) < n {
			return invalid
		}
		endpoint := string(p[:n])
		p = p[n:]
		host, port, err := net.SplitHostPort(endpoint)
		pn, pe := strconv.Atoi(port)
		ip := net.ParseIP(host)
		if err != nil || pe != nil || pn < 1 || pn > 65535 || ip == nil || !ip.IsLoopback() {
			return invalid
		}
		endpoints = append(endpoints, endpoint)
	}
	// Optional detail added to the new policy query. The original UDP v1
	// capability response remains exactly one byte and is authoritative.
	if len(p) == 1 && p[0] <= 4 {
		if c.Custom == "disabled" {
			switch p[0] {
			case 1:
				c.Reason = "E_UDP_ALLOW_DISABLED"
			case 2:
				c.Reason = "E_UDP_DISABLE_FORWARDING"
			case 3:
				c.Reason = "E_UDP_KEY_RESTRICTED"
			}
		}
		p = p[1:]
	}
	if len(p) != 0 {
		return invalid
	}
	c.PolicyKnown, c.BypassSupported, c.ForwardProxyEnabled, c.BypassEndpoints = true, true, enabled, endpoints
	return nil
}
