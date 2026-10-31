package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"net"
)

func probeUDPEcho(ctx context.Context, r request, out *result) error {
	// This deployment verifier must go through the temporary core. Never
	// allow a missing proxy to turn a verification into a local direct probe.
	token, err := hex.DecodeString(r.Query)
	host, _, hostErr := net.SplitHostPort(r.Target)
	ip := net.ParseIP(host)
	if err != nil || len(token) != 32 || r.Proxy == "" || hostErr != nil || ip == nil || !ip.IsLoopback() {
		return errors.New("invalid deployment echo request")
	}
	c, addr, err := openPacket(ctx, r, r.Target)
	if err != nil {
		return errors.New("E_UDP_ECHO_CONNECT")
	}
	defer c.Close()
	stop := context.AfterFunc(ctx, func() { c.Close() })
	defer stop()
	if deadline, ok := ctx.Deadline(); ok {
		c.SetDeadline(deadline)
	}
	if _, err = c.WriteTo(token, addr); err != nil {
		return errors.New("E_UDP_ECHO_SEND")
	}
	var response [64]byte
	n, _, err := c.ReadFrom(response[:])
	if err != nil || !bytes.Equal(response[:n], token) {
		return errors.New("E_UDP_ECHO_MISMATCH")
	}
	out.Protocol, out.Evidence = "UDP", "Deployment gateway returned the single-use 256-bit loopback echo token through the temporary SSH core"
	return nil
}
