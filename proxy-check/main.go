// proxy-check is a private, versioned stdio worker, never a listening service.
package main

import (
	"bufio"
	"context"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	observer "github.com/idefav/egress-probe"

	"github.com/quic-go/quic-go"
	"github.com/quic-go/quic-go/http3"
	"golang.org/x/net/dns/dnsmessage"
	"golang.org/x/net/proxy"
)

type request struct {
	Observer *observerRequest `json:"observer,omitempty"`
	SSH      *sshProbeOptions `json:"ssh,omitempty"`
	Version  int              `json:"version"`
	Kind     string           `json:"kind"`
	Target   string           `json:"target"`
	Proxy    string           `json:"proxy"`
	Family   string           `json:"family"`
	Query    string           `json:"query"`
	Timeout  int              `json:"timeout"`
}
type result struct {
	FailureStage string             `json:"failureStage,omitempty"`
	Receipts     []observer.Receipt `json:"receipts,omitempty"`
	Version      int                `json:"version"`
	State        string             `json:"state"`
	Elapsed      float64            `json:"elapsed"`
	Egress       string             `json:"egress,omitempty"`
	Protocol     string             `json:"protocol,omitempty"`
	Evidence     string             `json:"evidence,omitempty"`
	Error        string             `json:"error,omitempty"`
}

func main() {
	var r request
	if err := json.NewDecoder(io.LimitReader(os.Stdin, 65536)).Decode(&r); err != nil {
		_ = json.NewEncoder(os.Stdout).Encode(result{Version: 1, State: "failed", Error: "invalid request"})
		return
	}
	_ = json.NewEncoder(os.Stdout).Encode(run(r))
}

func run(r request) (out result) {
	out = result{Version: r.Version, State: "failed"}
	start := time.Now()
	defer func() { out.Elapsed = float64(time.Since(start).Microseconds()) / 1000 }()
	if (r.Version != 1 && r.Version != 2) || r.Timeout < 1 || r.Timeout > 120 || (r.Observer != nil && r.Version != 2) {
		out.Error = "unsupported version or timeout"
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(r.Timeout)*time.Second)
	defer cancel()
	var err error
	if r.Observer != nil && (r.Kind == "tcp" || r.Kind == "tls" || r.Kind == "udp") {
		err = probeObserver(ctx, r, &out)
	} else {
		switch r.Kind {
		case "ssh-capability":
			err = probeSSH(ctx, r.SSH, &out)
		case "tcp":
			var c net.Conn
			c, err = dialTCP(ctx, r, r.Target)
			if err == nil {
				out.Protocol = "TCP"
				out.Evidence = c.RemoteAddr().String()
				c.Close()
			}
		case "http", "https", "h3":
			err = probeHTTP(ctx, r, &out)
		case "stun":
			err = probeSTUN(ctx, r, &out)
		case "dns-udp", "dns-tcp":
			err = probeDNS(ctx, r, &out)
		default:
			err = errors.New("unsupported probe")
		}
	}
	if err != nil {
		out.Error = err.Error()
		// Never expose supplied proxy credentials in errors.
		if u, e := url.Parse(r.Proxy); e == nil && u.User != nil {
			out.Error = strings.ReplaceAll(out.Error, r.Proxy, u.Redacted())
		}
		if errors.Is(err, errUnsupported) {
			out.State = "unsupported"
		}
		return
	}
	out.State = "passed"
	return
}

var errUnsupported = errors.New("transport does not support this probe")

func dialTCP(ctx context.Context, r request, target string) (net.Conn, error) {
	d := &net.Dialer{}
	if r.Proxy == "" {
		return d.DialContext(ctx, "tcp"+r.Family, target)
	}
	u, err := url.Parse(r.Proxy)
	if err != nil {
		return nil, err
	}
	if u.Scheme == "socks5" {
		var auth *proxy.Auth
		if u.User != nil {
			p, _ := u.User.Password()
			auth = &proxy.Auth{User: u.User.Username(), Password: p}
		}
		dialer, e := proxy.SOCKS5("tcp", u.Host, auth, d)
		if e != nil {
			return nil, e
		}
		return dialer.(proxy.ContextDialer).DialContext(ctx, "tcp", target)
	}
	if u.Scheme != "http" {
		return nil, errUnsupported
	}
	c, err := d.DialContext(ctx, "tcp", u.Host)
	if err != nil {
		return nil, err
	}
	stop := context.AfterFunc(ctx, func() { c.Close() })
	defer stop()
	if deadline, ok := ctx.Deadline(); ok {
		_ = c.SetDeadline(deadline)
	}
	req := &http.Request{Method: "CONNECT", URL: &url.URL{Opaque: target}, Host: target, Header: make(http.Header)}
	if u.User != nil {
		p, _ := u.User.Password()
		req.SetBasicAuth(u.User.Username(), p)
		req.Header.Set("Proxy-Authorization", req.Header.Get("Authorization"))
		req.Header.Del("Authorization")
	}
	if err = req.Write(c); err != nil {
		c.Close()
		return nil, err
	}
	br := bufio.NewReader(c)
	resp, err := http.ReadResponse(br, req)
	if err != nil {
		c.Close()
		return nil, err
	}
	if resp.StatusCode != 200 {
		c.Close()
		return nil, fmt.Errorf("CONNECT status %d", resp.StatusCode)
	}
	return &bufferedConn{Conn: c, reader: br}, nil
}

type bufferedConn struct {
	net.Conn
	reader *bufio.Reader
}

func (c *bufferedConn) Read(p []byte) (int, error) { return c.reader.Read(p) }

func probeHTTP(ctx context.Context, r request, out *result) error {
	return probeHTTPWithRoots(ctx, r, out, nil)
}
func probeHTTPWithRoots(ctx context.Context, r request, out *result, roots *x509.CertPool) error {
	var stage atomic.Value
	stage.Store("")
	defer func() { out.FailureStage = stage.Load().(string) }()
	u, err := url.Parse(r.Target)
	if err != nil || u.Hostname() == "" || u.User != nil {
		return errors.New("invalid HTTP target")
	}
	if r.Kind == "http" && u.Scheme != "http" {
		return errors.New("HTTP probe requires http URL")
	}
	if r.Kind != "http" && u.Scheme != "https" {
		return errors.New("TLS probe requires https URL")
	}
	var transport http.RoundTripper
	var closeTransport func()
	if r.Kind == "h3" {
		h3 := &http3.Transport{TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS13, RootCAs: roots}, QUICConfig: &quic.Config{HandshakeIdleTimeout: time.Duration(r.Timeout) * time.Second}}
		h3.Dial = func(ctx context.Context, addr string, tc *tls.Config, qc *quic.Config) (*quic.Conn, error) {
			stage.Store("udp-channel")
			pc, remote, e := openPacket(ctx, r, addr)
			if e != nil {
				return nil, e
			}
			stage.Store("quic-tls-handshake")
			conn, e := quic.Dial(ctx, pc, remote, tc, qc)
			if e != nil {
				pc.Close()
				return nil, e
			}
			go func() { <-conn.Context().Done(); pc.Close() }()
			stage.Store("http3-request")
			return conn, nil
		}
		transport = h3
		closeTransport = func() { h3.Close() }
	} else {
		t := &http.Transport{Proxy: nil, DialContext: func(ctx context.Context, _, addr string) (net.Conn, error) { return dialTCP(ctx, r, addr) }, TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots}, ForceAttemptHTTP2: true, DisableKeepAlives: true}
		transport = t
		closeTransport = t.CloseIdleConnections
	}
	defer closeTransport()
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	req, err := http.NewRequestWithContext(ctx, "GET", r.Target, nil)
	if err != nil {
		return err
	}
	if r.Observer != nil {
		req.Header.Set("X-Probe-ID", r.Observer.Ticket.ID)
		req.Header.Set("X-Probe-Nonce", r.Observer.Ticket.Nonce)
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if r.Kind == "h3" && resp.ProtoMajor != 3 {
		return errors.New("HTTP/3 was not negotiated")
	}
	if resp.StatusCode != 200 {
		return fmt.Errorf("HTTP status %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 65537))
	if err != nil {
		return err
	}
	if len(body) > 65536 {
		return errors.New("response exceeds limit")
	}
	if r.Observer != nil {
		stage.Store("observer-evidence")
		var receipt observer.Receipt
		if err = json.Unmarshal(body, &receipt); err != nil {
			return err
		}
		o, e := observer.Verify(r.Observer.Profile, r.Observer.Ticket, receipt)
		if e != nil {
			return e
		}
		if r.Kind == "h3" && (o.Protocol != "HTTP/3.0" || o.ALPN != "h3") {
			return errors.New("observer did not confirm HTTP3")
		}
		out.Receipts = []observer.Receipt{receipt}
		out.Egress = o.PeerIP
		out.Protocol = resp.Proto
		out.Evidence = "signed observation on the same HTTP connection"
		stage.Store("")
		return nil
	}
	out.Protocol = resp.Proto
	for _, line := range strings.Split(string(body), "\n") {
		if strings.HasPrefix(line, "ip=") {
			out.Egress = strings.TrimSpace(strings.TrimPrefix(line, "ip="))
		}
	}
	if net.ParseIP(strings.TrimSpace(string(body))) != nil {
		out.Egress = strings.TrimSpace(string(body))
	}
	if out.Egress != "" && net.ParseIP(out.Egress) == nil {
		return errors.New("invalid egress address")
	}
	out.Evidence = fmt.Sprintf("status=%d; protocol=%s", resp.StatusCode, resp.Proto)
	if resp.TLS != nil {
		out.Evidence += fmt.Sprintf("; TLS=0x%x; ALPN=%s; certificate verified", resp.TLS.Version, resp.TLS.NegotiatedProtocol)
	}
	return nil
}

// PacketConn owns the SOCKS TCP control connection for the entire association.
type socksPacket struct {
	net.PacketConn
	control net.Conn
	relay   net.Addr
	target  string
	remote  net.Addr
}

func (s *socksPacket) Close() error { s.control.Close(); return s.PacketConn.Close() }
func (s *socksPacket) WriteTo(p []byte, a net.Addr) (int, error) {
	if a.String() != s.remote.String() {
		return 0, errors.New("unexpected destination")
	}
	h, e := encodeAddr(s.target)
	if e != nil {
		return 0, e
	}
	packet := append([]byte{0, 0, 0}, h...)
	packet = append(packet, p...)
	_, e = s.PacketConn.WriteTo(packet, s.relay)
	if e != nil {
		return 0, e
	}
	return len(p), nil
}
func (s *socksPacket) ReadFrom(p []byte) (int, net.Addr, error) {
	b := make([]byte, 65535)
	for {
		n, from, e := s.PacketConn.ReadFrom(b)
		if e != nil {
			return 0, nil, e
		}
		if from.String() != s.relay.String() {
			continue
		}
		if n < 4 || b[0] != 0 || b[1] != 0 || b[2] != 0 {
			return 0, nil, errors.New("invalid SOCKS datagram")
		}
		addr, used, e := decodeAddr(b[3:n])
		if e != nil {
			return 0, nil, e
		}
		// Literal destinations must match; domain replies may contain the resolved IP.
		h, p1, _ := net.SplitHostPort(s.target)
		h2, p2, _ := net.SplitHostPort(addr)
		if p1 != p2 || (net.ParseIP(h) != nil && !net.ParseIP(h).Equal(net.ParseIP(h2))) {
			continue
		}
		data := b[3+used : n]
		if len(data) > len(p) {
			return 0, nil, io.ErrShortBuffer
		}
		return copy(p, data), s.remote, nil
	}
}
func encodeAddr(target string) ([]byte, error) {
	h, p, e := net.SplitHostPort(target)
	if e != nil {
		return nil, e
	}
	port, e := strconv.ParseUint(p, 10, 16)
	if e != nil || port == 0 {
		return nil, errors.New("invalid port")
	}
	var b []byte
	if ip := net.ParseIP(h); ip != nil {
		if v4 := ip.To4(); v4 != nil {
			b = append([]byte{1}, v4...)
		} else {
			b = append([]byte{4}, ip.To16()...)
		}
	} else {
		if len(h) == 0 || len(h) > 255 {
			return nil, errors.New("invalid hostname")
		}
		b = append([]byte{3, byte(len(h))}, []byte(h)...)
	}
	return binary.BigEndian.AppendUint16(b, uint16(port)), nil
}
func decodeAddr(b []byte) (string, int, error) {
	if len(b) < 1 {
		return "", 0, io.ErrUnexpectedEOF
	}
	n := 0
	h := ""
	switch b[0] {
	case 1:
		n = 5
	case 4:
		n = 17
	case 3:
		if len(b) < 2 {
			return "", 0, io.ErrUnexpectedEOF
		}
		n = 2 + int(b[1])
	default:
		return "", 0, errors.New("invalid address type")
	}
	if len(b) < n+2 {
		return "", 0, io.ErrUnexpectedEOF
	}
	if b[0] == 3 {
		h = string(b[2:n])
	} else {
		h = net.IP(b[1:n]).String()
	}
	return net.JoinHostPort(h, strconv.Itoa(int(binary.BigEndian.Uint16(b[n:n+2])))), n + 2, nil
}
func readAddr(r io.Reader) (string, error) {
	var h [2]byte
	if _, e := io.ReadFull(r, h[:1]); e != nil {
		return "", e
	}
	b := []byte{h[0]}
	n := 0
	switch h[0] {
	case 1:
		n = 6
	case 4:
		n = 18
	case 3:
		if _, e := io.ReadFull(r, h[1:]); e != nil {
			return "", e
		}
		b = append(b, h[1])
		n = int(h[1]) + 2
	default:
		return "", errors.New("invalid SOCKS address")
	}
	t := make([]byte, n)
	if _, e := io.ReadFull(r, t); e != nil {
		return "", e
	}
	a, _, e := decodeAddr(append(b, t...))
	return a, e
}
func openPacket(ctx context.Context, r request, target string) (net.PacketConn, net.Addr, error) {
	if r.Proxy == "" {
		h, p, e := net.SplitHostPort(target)
		if e != nil {
			return nil, nil, e
		}
		ips, e := net.DefaultResolver.LookupIP(ctx, "ip"+r.Family, h)
		if e != nil || len(ips) == 0 {
			return nil, nil, fmt.Errorf("resolve UDP target: %v", e)
		}
		port, e := strconv.Atoi(p)
		if e != nil {
			return nil, nil, e
		}
		a := &net.UDPAddr{IP: ips[0], Port: port}
		network := "udp4"
		if a.IP.To4() == nil {
			network = "udp6"
		}
		c, e := net.ListenPacket(network, ":0")
		if e != nil {
			return nil, nil, e
		}
		deadline, _ := ctx.Deadline()
		c.SetDeadline(deadline)
		return c, a, nil
	}
	u, e := url.Parse(r.Proxy)
	if e != nil || u.Scheme != "socks5" {
		return nil, nil, errUnsupported
	}
	c, e := (&net.Dialer{}).DialContext(ctx, "tcp", u.Host)
	if e != nil {
		return nil, nil, e
	}
	ok := false
	defer func() {
		if !ok {
			c.Close()
		}
	}()
	deadline, _ := ctx.Deadline()
	c.SetDeadline(deadline)
	stop := context.AfterFunc(ctx, func() { c.Close() })
	defer stop()
	methods := []byte{5, 1, 0}
	if u.User != nil {
		methods = []byte{5, 2, 0, 2}
	}
	if _, e = c.Write(methods); e != nil {
		return nil, nil, e
	}
	var reply [2]byte
	if _, e = io.ReadFull(c, reply[:]); e != nil {
		return nil, nil, e
	}
	if reply[0] != 5 {
		return nil, nil, errors.New("invalid SOCKS version")
	}
	if reply[1] == 2 && u.User != nil {
		p, _ := u.User.Password()
		user := u.User.Username()
		if len(user) > 255 || len(p) > 255 {
			return nil, nil, errors.New("credentials too long")
		}
		b := append([]byte{1, byte(len(user))}, user...)
		b = append(b, byte(len(p)))
		b = append(b, p...)
		if _, e = c.Write(b); e != nil {
			return nil, nil, e
		}
		if _, e = io.ReadFull(c, reply[:]); e != nil {
			return nil, nil, e
		}
		if reply[0] != 1 || reply[1] != 0 {
			return nil, nil, errors.New("SOCKS authentication rejected")
		}
	} else if reply[1] != 0 {
		return nil, nil, errors.New("SOCKS method rejected")
	}
	if _, e = c.Write([]byte{5, 3, 0, 1, 0, 0, 0, 0, 0, 0}); e != nil {
		return nil, nil, e
	}
	var head [3]byte
	if _, e = io.ReadFull(c, head[:]); e != nil {
		return nil, nil, e
	}
	if head[0] != 5 || head[2] != 0 {
		return nil, nil, errors.New("invalid SOCKS reply")
	}
	if head[1] == 7 {
		return nil, nil, errUnsupported
	}
	if head[1] != 0 {
		return nil, nil, fmt.Errorf("SOCKS UDP association rejected: %d", head[1])
	}
	relayText, e := readAddr(c)
	if e != nil {
		return nil, nil, e
	}
	rh, rp, _ := net.SplitHostPort(relayText)
	if ip := net.ParseIP(rh); ip != nil && ip.IsUnspecified() {
		rh, _, _ = net.SplitHostPort(c.RemoteAddr().String())
	}
	relay, e := net.ResolveUDPAddr("udp", net.JoinHostPort(rh, rp))
	if e != nil {
		return nil, nil, e
	}
	network := "udp4"
	if relay.IP.To4() == nil {
		network = "udp6"
	}
	pc, e := net.ListenPacket(network, ":0")
	if e != nil {
		return nil, nil, e
	}
	pc.SetDeadline(deadline)
	c.SetDeadline(time.Time{})
	// quic-go requires a UDPAddr. The wire target retains domain form for remote DNS.
	h, p, _ := net.SplitHostPort(target)
	port, _ := strconv.Atoi(p)
	ip := net.ParseIP(h)
	if ip == nil {
		ip = net.IPv4(0, 0, 0, 1)
	}
	remote := &net.UDPAddr{IP: ip, Port: port}
	s := &socksPacket{PacketConn: pc, control: c, relay: relay, target: target, remote: remote}
	ok = true
	go func() { _, _ = io.Copy(io.Discard, c); pc.Close() }()
	return s, remote, nil
}

func probeSTUN(ctx context.Context, r request, out *result) error {
	c, a, e := openPacket(ctx, r, r.Target)
	if e != nil {
		return e
	}
	defer c.Close()
	b := make([]byte, 20)
	binary.BigEndian.PutUint16(b, 1)
	binary.BigEndian.PutUint32(b[4:], 0x2112a442)
	if _, e = rand.Read(b[8:]); e != nil {
		return e
	}
	if r.Observer != nil {
		b = observer.STUNRequest(r.Observer.Ticket, b[8:20])
	}
	if _, e = c.WriteTo(b, a); e != nil {
		return e
	}
	buf := make([]byte, 65535)
	n, from, e := c.ReadFrom(buf)
	if e != nil {
		return e
	}
	if from.String() != a.String() {
		return errors.New("STUN source mismatch")
	}
	ip, e := parseSTUN(buf[:n], b[8:20])
	if e != nil {
		return e
	}
	out.Egress = ip
	out.Protocol = "STUN/UDP"
	out.Evidence = "matched transaction; mapped address observed by STUN server"
	return nil
}
func parseSTUN(b, id []byte) (string, error) {
	if len(b) < 20 || len(id) != 12 || binary.BigEndian.Uint16(b) != 0x101 || binary.BigEndian.Uint32(b[4:]) != 0x2112a442 || hex.EncodeToString(b[8:20]) != hex.EncodeToString(id) {
		return "", errors.New("invalid STUN response or transaction")
	}
	n := int(binary.BigEndian.Uint16(b[2:]))
	if n%4 != 0 || n != len(b)-20 {
		return "", errors.New("invalid STUN length")
	}
	for pos := 20; pos < len(b); {
		if pos+4 > len(b) {
			return "", io.ErrUnexpectedEOF
		}
		t := binary.BigEndian.Uint16(b[pos:])
		l := int(binary.BigEndian.Uint16(b[pos+2:]))
		pos += 4
		if pos+l > len(b) {
			return "", io.ErrUnexpectedEOF
		}
		v := b[pos : pos+l]
		if t == 0x20 || t == 1 {
			if l < 4 {
				return "", io.ErrUnexpectedEOF
			}
			size := 4
			if v[1] == 2 {
				size = 16
			} else if v[1] != 1 {
				return "", errors.New("invalid STUN family")
			}
			if l != size+4 {
				return "", errors.New("invalid STUN address")
			}
			ip := append([]byte(nil), v[4:]...)
			if t == 0x20 {
				mask := append([]byte{0x21, 0x12, 0xa4, 0x42}, id...)
				for i := range ip {
					ip[i] ^= mask[i]
				}
			}
			return net.IP(ip).String(), nil
		}
		pos += (l + 3) &^ 3
	}
	return "", errors.New("STUN mapped address missing")
}

func probeDNS(ctx context.Context, r request, out *result) error {
	name := "example.com."
	typ := dnsmessage.TypeA
	class := dnsmessage.ClassINET
	switch r.Query {
	case "AAAA":
		typ = dnsmessage.TypeAAAA
	case "NXDOMAIN":
		var b [12]byte
		rand.Read(b[:])
		name = hex.EncodeToString(b[:]) + ".invalid."
	case "WHOAMI":
		name = "whoami.cloudflare."
		typ = dnsmessage.TypeTXT
		class = dnsmessage.ClassCHAOS
	}
	if r.Observer != nil {
		name = r.Observer.Ticket.QueryName
		class = dnsmessage.ClassINET
	}
	var id [2]byte
	rand.Read(id[:])
	q := dnsmessage.Question{Name: dnsmessage.MustNewName(name), Type: typ, Class: class}
	m := dnsmessage.Message{Header: dnsmessage.Header{ID: binary.BigEndian.Uint16(id[:]), RecursionDesired: true}, Questions: []dnsmessage.Question{q}}
	wire, _ := m.Pack()
	viaTCP := r.Kind == "dns-tcp"
	for attempts := 0; attempts < 2; attempts++ {
		var reply []byte
		if viaTCP {
			c, e := dialTCP(ctx, r, r.Target)
			if e != nil {
				return e
			}
			deadline, _ := ctx.Deadline()
			c.SetDeadline(deadline)
			defer c.Close()
			if _, e = c.Write(append(binary.BigEndian.AppendUint16(nil, uint16(len(wire))), wire...)); e != nil {
				return e
			}
			var size [2]byte
			if _, e = io.ReadFull(c, size[:]); e != nil {
				return e
			}
			reply = make([]byte, binary.BigEndian.Uint16(size[:]))
			if _, e = io.ReadFull(c, reply); e != nil {
				return e
			}
		} else {
			c, a, e := openPacket(ctx, r, r.Target)
			if e != nil {
				return e
			}
			defer c.Close()
			if _, e = c.WriteTo(wire, a); e != nil {
				return e
			}
			b := make([]byte, 65535)
			for {
				n, from, e := c.ReadFrom(b)
				if e != nil {
					return e
				}
				if from.String() == a.String() {
					reply = b[:n]
					break
				}
			}
		}
		var ans dnsmessage.Message
		if e := ans.Unpack(reply); e != nil {
			return e
		}
		if !ans.Response || ans.ID != m.ID || ans.OpCode != 0 || len(ans.Questions) != 1 || ans.Questions[0] != q {
			return errors.New("DNS response mismatch")
		}
		if ans.Truncated && !viaTCP {
			viaTCP = true
			continue
		}
		if ans.Truncated {
			return errors.New("truncated DNS TCP response")
		}
		if r.Query == "NXDOMAIN" {
			if ans.RCode != dnsmessage.RCodeNameError {
				return errors.New("expected NXDOMAIN")
			}
		} else if ans.RCode != dnsmessage.RCodeSuccess {
			return fmt.Errorf("DNS rcode=%v", ans.RCode)
		} else if len(ans.Answers) == 0 {
			return errors.New("DNS answer missing")
		}
		if r.Query != "NXDOMAIN" {
			found := false
			for _, answer := range ans.Answers {
				if answer.Header.Type == typ && answer.Header.Class == class {
					found = true
				}
			}
			if !found {
				return errors.New("requested DNS record type missing")
			}
		}
		out.Protocol = "DNS/UDP"
		if viaTCP {
			out.Protocol = "DNS/TCP"
		}
		out.Evidence = fmt.Sprintf("query=%s; rcode=%v; answers=%d; matched transaction", name, ans.RCode, len(ans.Answers))
		if r.Query == "WHOAMI" {
			for _, a := range ans.Answers {
				if txt, ok := a.Body.(*dnsmessage.TXTResource); ok {
					for _, s := range txt.TXT {
						if net.ParseIP(s) != nil {
							out.Egress = s
						}
					}
				}
			}
			out.Evidence += "; resolver-observed source for this DNS request only"
		}
		return nil
	}
	return errors.New("DNS retry exhausted")
}
