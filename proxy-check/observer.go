package main

import (
	"context"
	"net"

	observer "github.com/idefav/egress-probe"
)

type observerRequest struct {
	Profile observer.Profile `json:"profile"`
	Ticket  observer.Ticket  `json:"ticket"`
}

func probeObserver(ctx context.Context, r request, out *result) error {
	var receipt observer.Receipt
	var err error
	if r.Kind == "udp" {
		receipt, err = observer.ProbeUDP(ctx, r.Observer.Profile, r.Observer.Ticket, func(ctx context.Context, target string) (net.PacketConn, net.Addr, error) {
			return openPacket(ctx, r, target)
		})
	} else {
		receipt, err = observer.ProbeStream(ctx, r.Observer.Profile, r.Observer.Ticket, func(ctx context.Context, target string) (net.Conn, error) { return dialTCP(ctx, r, target) })
	}
	if err != nil {
		return err
	}
	o, err := observer.Verify(r.Observer.Profile, r.Observer.Ticket, receipt)
	if err != nil {
		return err
	}
	out.Receipts = []observer.Receipt{receipt}
	out.Egress = o.PeerIP
	out.Protocol = o.Protocol
	out.Evidence = "signed observation on the same connection/datagram"
	return nil
}
