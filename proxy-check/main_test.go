package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"encoding/pem"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"sync"
	"testing"
	"time"

	"github.com/quic-go/quic-go/http3"
	"golang.org/x/net/dns/dnsmessage"
)

func stunReply(id []byte, ip net.IP) []byte {
	b := make([]byte, 20)
	binary.BigEndian.PutUint16(b, 0x101)
	binary.BigEndian.PutUint32(b[4:], 0x2112a442)
	copy(b[8:], id)
	family := byte(1)
	if ip.To4() != nil {
		ip = ip.To4()
	} else {
		family = 2
		ip = ip.To16()
	}
	v := []byte{0, family, 0x12, 0x34}
	mask := append([]byte{0x21, 0x12, 0xa4, 0x42}, id...)
	for i, c := range ip {
		v = append(v, c^mask[i])
	}
	b = binary.BigEndian.AppendUint16(b, 0x20)
	b = binary.BigEndian.AppendUint16(b, uint16(len(v)))
	b = append(b, v...)
	binary.BigEndian.PutUint16(b[2:], uint16(len(b)-20))
	return b
}
func TestSTUNValidation(t *testing.T) {
	id := make([]byte, 12)
	rand.Read(id)
	for _, ip := range []string{"203.0.113.19", "2001:db8::19"} {
		b := stunReply(id, net.ParseIP(ip))
		got, e := parseSTUN(b, id)
		if e != nil || got != ip {
			t.Fatal(got, e)
		}
		for i := range b {
			if _, e := parseSTUN(b[:i], id); e == nil {
				t.Fatalf("accepted truncated response %d", i)
			}
		}
		wrong := append([]byte(nil), id...)
		wrong[0] ^= 1
		if _, e := parseSTUN(b, wrong); e == nil {
			t.Fatal("accepted wrong transaction")
		}
		b[0] = 1
		b[1] = 0x11
		if _, e := parseSTUN(b, id); e == nil {
			t.Fatal("accepted error response")
		}
	}
}
func TestAddressCodec(t *testing.T) {
	for _, s := range []string{"127.0.0.1:1234", "[::1]:65535", "example.com:53"} {
		b, e := encodeAddr(s)
		if e != nil {
			t.Fatal(e)
		}
		got, n, e := decodeAddr(b)
		if e != nil || got != s || n != len(b) {
			t.Fatal(got, n, e)
		}
		for i := range b {
			if _, _, e := decodeAddr(b[:i]); e == nil {
				t.Fatal("accepted truncated SOCKS address")
			}
		}
	}
}
func TestHTTPAndConnectRejection(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == "CONNECT" {
			w.WriteHeader(407)
			return
		}
		io.WriteString(w, "ip=203.0.113.7\n")
	}))
	defer server.Close()
	r := run(request{Version: 1, Kind: "http", Target: server.URL, Timeout: 2})
	if r.State != "passed" || r.Egress != "203.0.113.7" {
		t.Fatal(r)
	}
	r = run(request{Version: 1, Kind: "tcp", Target: "example.com:443", Proxy: server.URL, Timeout: 2})
	if r.State != "failed" {
		t.Fatal(r)
	}
	r = run(request{Version: 1, Kind: "h3", Target: server.URL, Timeout: 1})
	if r.State != "failed" {
		t.Fatal("HTTP3 fell back", r)
	}
}
func TestDNSUDPTruncationRetriesTCP(t *testing.T) {
	tcp, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer tcp.Close()
	udp, e := net.ListenPacket("udp", tcp.Addr().String())
	if e != nil {
		t.Fatal(e)
	}
	defer udp.Close()
	makeReply := func(b []byte, truncated bool) []byte {
		var q dnsmessage.Message
		if e := q.Unpack(b); e != nil {
			return nil
		}
		q.Response = true
		q.Truncated = truncated
		if !truncated {
			q.Answers = []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: q.Questions[0].Name, Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET, TTL: 60}, Body: &dnsmessage.AResource{A: [4]byte{203, 0, 113, 7}}}}
		}
		out, _ := q.Pack()
		return out
	}
	go func() {
		b := make([]byte, 4096)
		n, a, e := udp.ReadFrom(b)
		if e == nil {
			udp.WriteTo(makeReply(b[:n], true), a)
		}
	}()
	go func() {
		c, e := tcp.Accept()
		if e != nil {
			return
		}
		defer c.Close()
		var size [2]byte
		io.ReadFull(c, size[:])
		b := make([]byte, binary.BigEndian.Uint16(size[:]))
		io.ReadFull(c, b)
		reply := makeReply(b, false)
		c.Write(append(binary.BigEndian.AppendUint16(nil, uint16(len(reply))), reply...))
	}()
	r := run(request{Version: 1, Kind: "dns-udp", Target: tcp.Addr().String(), Query: "A", Timeout: 2})
	if r.State != "passed" || r.Protocol != "DNS/TCP" {
		t.Fatal(r)
	}
}
func fixtureCert(t *testing.T) (tls.Certificate, *x509.CertPool) {
	t.Helper()
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "localhost"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, IsCA: true, BasicConstraintsValid: true, IPAddresses: []net.IP{net.ParseIP("127.0.0.1")}, DNSNames: []string{"localhost"}}
	der, e := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if e != nil {
		t.Fatal(e)
	}
	priv, _ := x509.MarshalPKCS8PrivateKey(key)
	cert, e := tls.X509KeyPair(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: priv}))
	if e != nil {
		t.Fatal(e)
	}
	pool := x509.NewCertPool()
	parsed, _ := x509.ParseCertificate(der)
	pool.AddCert(parsed)
	return cert, pool
}

// A local SOCKS5 UDP fixture forwards packets and owns each association's lifetime.
func socksFixture(t *testing.T, auth bool) (string, func()) {
	t.Helper()
	ln, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	var mu sync.Mutex
	var conns []net.Conn
	var packets []net.PacketConn
	go func() {
		for {
			c, e := ln.Accept()
			if e != nil {
				return
			}
			mu.Lock()
			conns = append(conns, c)
			mu.Unlock()
			go func() {
				defer c.Close()
				var head [2]byte
				if _, e := io.ReadFull(c, head[:]); e != nil {
					return
				}
				m := make([]byte, int(head[1]))
				io.ReadFull(c, m)
				if auth {
					c.Write([]byte{5, 2})
					var ah [2]byte
					io.ReadFull(c, ah[:])
					user := make([]byte, ah[1])
					io.ReadFull(c, user)
					var plen [1]byte
					io.ReadFull(c, plen[:])
					pass := make([]byte, plen[0])
					io.ReadFull(c, pass)
					if string(user) != "test" || string(pass) != "secret" {
						c.Write([]byte{1, 1})
						return
					}
					c.Write([]byte{1, 0})
				} else {
					c.Write([]byte{5, 0})
				}
				var req [3]byte
				if _, e := io.ReadFull(c, req[:]); e != nil {
					return
				}
				readAddr(c)
				if req[1] != 3 {
					return
				}
				pc, e := net.ListenPacket("udp", "127.0.0.1:0")
				if e != nil {
					return
				}
				defer pc.Close()
				mu.Lock()
				packets = append(packets, pc)
				mu.Unlock()
				addr, _ := encodeAddr(pc.LocalAddr().String())
				c.Write(append([]byte{5, 0, 0}, addr...))
				go func() { io.Copy(io.Discard, c); pc.Close() }()
				b := make([]byte, 65535)
				var client net.Addr
				for {
					n, from, e := pc.ReadFrom(b)
					if e != nil {
						return
					}
					if client == nil {
						client = from
					}
					if from.String() == client.String() {
						if n < 4 {
							continue
						}
						target, used, e := decodeAddr(b[3:n])
						if e != nil {
							continue
						}
						a, e := net.ResolveUDPAddr("udp", target)
						if e != nil {
							continue
						}
						pc.WriteTo(b[3+used:n], a)
					} else {
						h, _ := encodeAddr(from.String())
						pc.WriteTo(append(append([]byte{0, 0, 0}, h...), b[:n]...), client)
					}
				}
			}()
		}
	}()
	close := func() {
		ln.Close()
		mu.Lock()
		defer mu.Unlock()
		for _, c := range conns {
			c.Close()
		}
		for _, p := range packets {
			p.Close()
		}
	}
	u := "socks5://" + ln.Addr().String()
	if auth {
		u = "socks5://test:secret@" + ln.Addr().String()
	}
	return u, close
}
func TestSTUNThroughSOCKSAndAuthentication(t *testing.T) {
	pc, e := net.ListenPacket("udp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer pc.Close()
	go func() {
		b := make([]byte, 1024)
		for {
			n, a, e := pc.ReadFrom(b)
			if e != nil {
				return
			}
			if n >= 20 {
				pc.WriteTo(stunReply(b[8:20], net.ParseIP("203.0.113.99")), a)
			}
		}
	}()
	for _, auth := range []bool{false, true} {
		u, close := socksFixture(t, auth)
		r := run(request{Version: 1, Kind: "stun", Target: pc.LocalAddr().String(), Proxy: u, Timeout: 2})
		close()
		if r.State != "passed" || r.Egress != "203.0.113.99" {
			t.Fatal(r)
		}
	}
	u, close := socksFixture(t, true)
	defer close()
	r := run(request{Version: 1, Kind: "stun", Target: pc.LocalAddr().String(), Proxy: "socks5://test:wrong@" + u[len("socks5://test:secret@"):], Timeout: 2})
	if r.State != "failed" {
		t.Fatal(r)
	}
}
func TestRealHTTP3NativeAndSOCKS(t *testing.T) {
	cert, pool := fixtureCert(t)
	pc, e := net.ListenPacket("udp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	server := &http3.Server{TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert}}, Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.ProtoMajor != 3 {
			t.Error("not HTTP3")
		}
		io.WriteString(w, "ip=203.0.113.88\n")
	})}
	go server.Serve(pc)
	defer server.Close()
	proxy, close := socksFixture(t, false)
	defer close()
	for _, p := range []string{"", proxy} {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		var out result
		e := probeHTTPWithRoots(ctx, request{Kind: "h3", Target: "https://" + pc.LocalAddr().String(), Proxy: p, Timeout: 3}, &out, pool)
		cancel()
		if e != nil || out.Protocol != "HTTP/3.0" || out.Egress != "203.0.113.88" {
			t.Fatal(out, e)
		}
	}
	// An untrusted fixture certificate must fail normal production verification.
	r := run(request{Version: 1, Kind: "h3", Target: "https://" + pc.LocalAddr().String(), Timeout: 2})
	if r.State != "failed" {
		t.Fatal("accepted invalid certificate", r)
	}
}
func TestCancellationAndUnsupported(t *testing.T) {
	r := run(request{Version: 2, Kind: "tcp", Target: "127.0.0.1:1", Timeout: 1})
	if r.State != "failed" {
		t.Fatal(r)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, e := dialTCP(ctx, request{}, "127.0.0.1:1"); e == nil {
		t.Fatal("ignored cancellation")
	}
	r = run(request{Version: 1, Kind: "stun", Target: "127.0.0.1:1", Proxy: "http://127.0.0.1:9", Timeout: 1})
	if r.State != "unsupported" {
		t.Fatal(r)
	}
	pc, e := net.ListenPacket("udp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer pc.Close()
	start := time.Now()
	r = run(request{Version: 1, Kind: "stun", Target: net.JoinHostPort("127.0.0.1", strconv.Itoa(pc.LocalAddr().(*net.UDPAddr).Port)), Timeout: 1})
	if r.State != "failed" || time.Since(start) > 2*time.Second {
		t.Fatal("timeout not bounded", r)
	}
}
