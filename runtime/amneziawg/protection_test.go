package main

import (
	"errors"
	"net"
	"testing"
)

func TestProvisionedTransportFailureDoesNotFallBackToUnprotectedSocket(t *testing.T) {
	bind := &udpBind{openSocket: func() (*net.UDPConn, error) { return nil, errors.New("host socket unavailable") }}
	if _, _, err := bind.Open(0); err == nil || bind.socket != nil {
		t.Fatal("host socket failure opened a fallback transport")
	}
}

func TestProvisionedTransportRetainsHostPortAndClosesItsOwnedSocket(t *testing.T) {
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer socket.Close()
	bind := &udpBind{openSocket: func() (*net.UDPConn, error) { return socket, nil }}
	_, port, err := bind.Open(0)
	if err != nil {
		t.Fatal(err)
	}
	if port != uint16(socket.LocalAddr().(*net.UDPAddr).Port) {
		t.Fatal("host port was replaced")
	}
	if err := bind.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err := socket.WriteToUDP([]byte{1}, socket.LocalAddr().(*net.UDPAddr)); err == nil {
		t.Fatal("owned socket remained open")
	}
}

func TestTransportCannotOpenWhenAndroidProtectionFails(t *testing.T) {
	called := false
	bind := &udpBind{protect: func(uintptr) bool { called = true; return false }}
	if _, _, err := bind.Open(0); err == nil {
		t.Fatal("unprotected transport opened")
	}
	if !called {
		t.Fatal("protection callback not invoked")
	}
	if bind.socket != nil {
		t.Fatal("failed transport retained its socket")
	}
}

func TestProtectionRunsBeforeTransportBecomesAvailable(t *testing.T) {
	bind := &udpBind{}
	bind.protect = func(fd uintptr) bool {
		if bind.socket != nil {
			t.Fatal("socket became available before protection")
		}
		return fd != 0
	}
	if _, _, err := bind.Open(0); err != nil {
		t.Fatal(err)
	}
	defer bind.Close()
}

func TestAndroidBridgeAllowsIpv6OuterEndpoint(t *testing.T) {
	bind := &udpBind{ipv6: true}
	endpoint, err := bind.ParseEndpoint("[::1]:51820")
	if err != nil || !endpoint.DstIP().Is6() {
		t.Fatalf("IPv6 endpoint rejected: %v", err)
	}
	if _, err := (&udpBind{}).ParseEndpoint("[::1]:51820"); err == nil {
		t.Fatal("Windows IPv4 restriction lost")
	}
	if _, _, err := bind.Open(0); err != nil {
		t.Skipf("IPv6 unavailable on this host: %v", err)
	}
	defer bind.Close()
	if bind.socket.LocalAddr().(*net.UDPAddr).IP.To4() != nil {
		t.Fatal("IPv6 transport opened an IPv4 socket")
	}
}
