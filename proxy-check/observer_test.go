package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"

	observer "github.com/idefav/egress-probe"
	observerServer "github.com/idefav/egress-probe/server"
	"github.com/quic-go/quic-go/http3"
)

func observerFixture(t *testing.T) (*observerServer.Server, *ecdsa.PrivateKey) {
	t.Helper()
	key, e := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	s, e := observerServer.New(observerServer.Profile("check.example.net", "203.0.113.10", "worker-fixture"), key, strings.Repeat("k", 64))
	if e != nil {
		t.Fatal(e)
	}
	return s, key
}
func observerTicket(t *testing.T, s *observerServer.Server, kind string) observer.Ticket {
	t.Helper()
	call := func(path, body string) []byte {
		r := httptest.NewRequest("POST", "https://api.check.example.net"+path, strings.NewReader(body))
		r.TLS = &tls.ConnectionState{}
		r.Header.Set("Authorization", "Bearer "+strings.Repeat("k", 64))
		w := httptest.NewRecorder()
		s.Handler().ServeHTTP(w, r)
		if w.Code != 200 {
			t.Fatal(w.Code, w.Body.String())
		}
		return w.Body.Bytes()
	}
	var sess struct {
		ID string `json:"id"`
	}
	json.Unmarshal(call("/v1/sessions", "{}"), &sess)
	var ticket observer.Ticket
	body, _ := json.Marshal(map[string]string{"kind": kind})
	json.Unmarshal(call("/v1/sessions/"+sess.ID+"/probes", string(body)), &ticket)
	return ticket
}

func TestSignedHTTP3NativeAndAuthenticatedSOCKS(t *testing.T) {
	s, _ := observerFixture(t)
	cert, pool := fixtureCert(t)
	pc, e := net.ListenPacket("udp4", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	server := &http3.Server{TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert}}, Handler: s.Handler()}
	go server.Serve(pc)
	defer server.Close()
	proxy, closeProxy := socksFixture(t, true)
	defer closeProxy()
	for _, path := range []string{"", proxy} {
		ticket := observerTicket(t, s, "h3")
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		var out result
		e := probeHTTPWithRoots(ctx, request{Version: 2, Kind: "h3", Target: "https://" + pc.LocalAddr().String() + "/v1/echo", Proxy: path, Timeout: 3, Observer: &observerRequest{s.Profile, ticket}}, &out, pool)
		cancel()
		if e != nil || len(out.Receipts) != 1 || out.Egress != "127.0.0.1" || out.Protocol != "HTTP/3.0" {
			t.Fatal(out, e)
		}
		o, e := observer.Verify(s.Profile, ticket, out.Receipts[0])
		if e != nil || o.ALPN != "h3" {
			t.Fatal(o, e)
		}
	}
}

func TestSignedTCPUsesSameCONNECTConnection(t *testing.T) {
	s, key := observerFixture(t)
	ticket := observerTicket(t, s, "tcp")
	l, e := net.Listen("tcp4", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer l.Close()
	s.Profile.TCPTarget = l.Addr().String()
	go func() {
		c, e := l.Accept()
		if e != nil {
			return
		}
		defer c.Close()
		var message observer.Message
		if json.NewDecoder(c).Decode(&message) != nil {
			return
		}
		h, p, _ := net.SplitHostPort(c.RemoteAddr().String())
		port, _ := strconv.Atoi(p)
		receipt, _ := observer.Sign(key, observer.Observation{Version: 1, ServerID: s.Profile.ServerID, SessionID: ticket.SessionID, ProbeID: message.ID, Nonce: message.Nonce, Kind: "tcp", Protocol: "TCP", PeerIP: h, PeerPort: port, ObservedAt: time.Now().UTC()})
		json.NewEncoder(c).Encode(receipt)
	}()
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "CONNECT" {
			w.WriteHeader(400)
			return
		}
		up, e := net.Dial("tcp", r.Host)
		if e != nil {
			w.WriteHeader(502)
			return
		}
		defer up.Close()
		c, buf, e := w.(http.Hijacker).Hijack()
		if e != nil {
			return
		}
		defer c.Close()
		buf.WriteString("HTTP/1.1 200 Connection Established\r\n\r\n")
		buf.Flush()
		go io.Copy(up, buf)
		io.Copy(c, up)
	}))
	defer proxy.Close()
	out := run(request{Version: 2, Kind: "tcp", Target: s.Profile.TCPTarget, Proxy: proxy.URL, Timeout: 2, Observer: &observerRequest{s.Profile, ticket}})
	if out.State != "passed" || out.Egress != "127.0.0.1" || len(out.Receipts) != 1 {
		t.Fatal(out)
	}
}
