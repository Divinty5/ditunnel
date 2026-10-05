package main

import (
	"bufio"
	"bytes"
	"crypto/tls"
	"crypto/x509"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"testing"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

// Exercise a browser-sized hybrid ClientHello and large TLS records through the
// encrypted AWG netstack, optionally including Xray's real SOCKS outbound.
// Everything remains on loopback or in-memory TUNs; no external server is used.
func checkTLSForwarding(t *testing.T, serverNet *netstack.Net, host string, openClient func() net.Conn) {
	t.Helper()
	target := netip.MustParseAddrPort(net.JoinHostPort(host, "0"))
	listener, err := serverNet.ListenTCPAddrPort(target)
	if err != nil {
		t.Fatal(err)
	}
	target, err = netip.ParseAddrPort(listener.Addr().String())
	if err != nil {
		listener.Close()
		t.Fatal(err)
	}
	payload := bytes.Repeat([]byte("AWG encrypted TLS payload\x00"), 8192)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(io.LimitReader(r.Body, int64(len(payload))+1))
		if err != nil || !bytes.Equal(body, payload) {
			http.Error(w, "payload mismatch", http.StatusBadRequest)
			return
		}
		w.Write(payload)
	}))
	server.Listener.Close()
	server.Listener = listener
	server.TLS = &tls.Config{MinVersion: tls.VersionTLS13, CurvePreferences: []tls.CurveID{tls.X25519MLKEM768}}
	server.StartTLS()
	defer server.Close()
	roots := x509.NewCertPool()
	roots.AddCert(server.Certificate())
	connection := openClient()
	defer connection.Close()
	request(t, connection, 1, target)
	recorded := &firstWriteRecorder{Conn: connection}
	secure := tls.Client(recorded, &tls.Config{
		RootCAs: roots, ServerName: "example.com", MinVersion: tls.VersionTLS13,
		CurvePreferences: []tls.CurveID{tls.X25519MLKEM768},
	})
	defer secure.Close()
	if err := secure.Handshake(); err != nil {
		t.Fatal("TLS handshake:", err)
	}
	if recorded.firstWrite <= 1280 {
		t.Fatalf("ClientHello did not exceed the tunnel MTU: %d bytes", recorded.firstWrite)
	}
	req, err := http.NewRequest(http.MethodPost, "https://example.com/bulk", bytes.NewReader(payload))
	if err != nil {
		t.Fatal(err)
	}
	req.Close = true
	if err := req.Write(secure); err != nil {
		t.Fatal("TLS request:", err)
	}
	response, err := http.ReadResponse(bufio.NewReader(secure), req)
	if err != nil {
		t.Fatal("TLS response:", err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil || response.StatusCode != http.StatusOK || !bytes.Equal(body, payload) {
		t.Fatalf("TLS payload truncated or changed: status=%d bytes=%d error=%v", response.StatusCode, len(body), err)
	}
}

type firstWriteRecorder struct {
	net.Conn
	firstWrite int
}

func (c *firstWriteRecorder) Write(data []byte) (int, error) {
	if c.firstWrite == 0 {
		c.firstWrite = len(data)
	}
	return c.Conn.Write(data)
}
