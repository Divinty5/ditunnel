package main

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/netip"
	"sync/atomic"
	"testing"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"golang.org/x/net/dns/dnsmessage"
)

// This authoritative resolver exists only inside the server's virtual TUN.
// The .invalid name cannot be resolved by the OS or by a public resolver.
func checkTunnelDNS(t *testing.T, client, server *netstack.Net) {
	t.Helper()
	resolver, err := server.ListenUDPAddrPort(netip.MustParseAddrPort("192.0.2.2:53"))
	if err != nil {
		t.Fatal(err)
	}
	defer resolver.Close()
	var queries atomic.Int32
	go func() {
		packet := make([]byte, 2048)
		for {
			n, source, err := resolver.ReadFrom(packet)
			if err != nil {
				return
			}
			var parser dnsmessage.Parser
			header, err := parser.Start(packet[:n])
			if err != nil {
				continue
			}
			question, err := parser.Question()
			if err != nil || question.Name.String() != "only-in-awg.invalid." {
				continue
			}
			builder := dnsmessage.NewBuilder(nil, dnsmessage.Header{ID: header.ID, Response: true, Authoritative: true, RecursionDesired: header.RecursionDesired, RecursionAvailable: true})
			builder.EnableCompression()
			builder.StartQuestions()
			builder.Question(question)
			builder.StartAnswers()
			resource := dnsmessage.ResourceHeader{Name: question.Name, Class: dnsmessage.ClassINET, TTL: 60}
			if question.Type == dnsmessage.TypeA {
				builder.AResource(resource, dnsmessage.AResource{A: [4]byte{192, 0, 2, 2}})
			} else if question.Type == dnsmessage.TypeAAAA {
				builder.AAAAResource(resource, dnsmessage.AAAAResource{AAAA: netip.MustParseAddr("2001:db8::2").As16()})
			}
			response, err := builder.Finish()
			if err == nil {
				queries.Add(1)
				resolver.WriteTo(response, source)
			}
		}
	}()
	listener, err := server.ListenTCPAddrPort(netip.MustParseAddrPort("192.0.2.2:9003"))
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		connection, err := listener.Accept()
		if err == nil {
			defer connection.Close()
			io.Copy(connection, connection)
		}
	}()
	proxy, err := newSOCKSServer(client, testUser, testPassword)
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go proxy.serve(ctx)
	connection := authClient(t, proxy.port())
	name := "only-in-awg.invalid"
	frame := append([]byte{5, 1, 0, 3, byte(len(name))}, name...)
	connection.Write(append(frame, 0x23, 0x2b)) // port 9003
	var header [3]byte
	if _, err := io.ReadFull(connection, header[:]); err != nil || header != [3]byte{5, 0, 0} {
		t.Fatalf("DNS tunnel CONNECT: %v %v", header, err)
	}
	if _, err := readAddress(connection); err != nil {
		t.Fatal(err)
	}
	connection.Write([]byte("DNS"))
	var echo [3]byte
	if _, err := io.ReadFull(connection, echo[:]); err != nil || string(echo[:]) != "DNS" {
		t.Fatalf("DNS TCP echo: %v", err)
	}
	if queries.Load() == 0 {
		t.Fatal("virtual resolver was bypassed")
	}
}

func TestUnreachableAwgNeverUsesPhysicalSocket(t *testing.T) {
	guard, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer guard.Close()
	sink, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer sink.Close()
	private, _ := testKeys(t, 1)
	_, public := testKeys(t, 2)
	uapi := fmt.Sprintf("private_key=%s\njc=2\npublic_key=%s\nendpoint=%s\nallowed_ip=127.0.0.1/32\n", private, public, sink.LocalAddr())
	dev, network, err := createDevice(testConfig(uapi, "192.0.2.1"), false)
	if err != nil {
		t.Fatal(err)
	}
	defer dev.Close()
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}
	proxy, err := newSOCKSServer(network, testUser, testPassword)
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.close()
	ctx, cancel := context.WithTimeout(context.Background(), 1500*time.Millisecond)
	defer cancel()
	go proxy.serve(ctx)
	connection := authClient(t, proxy.port())
	connection.Write(append([]byte{5, 1, 0}, encodeAddress(guard.Addr().(*net.TCPAddr).AddrPort())...))
	// The parent cancellation may close SOCKS before its error reply is written.
	var response [3]byte
	_, err = io.ReadFull(connection, response[:])
	if err == nil && response[1] == 0 {
		t.Fatal("unavailable AWG accepted CONNECT")
	}
	guard.SetDeadline(time.Now().Add(100 * time.Millisecond))
	unexpected, err := guard.Accept()
	if err == nil {
		unexpected.Close()
		t.Fatal("AWG fell back to the physical network")
	}
	if timeout, ok := err.(net.Error); !ok || !timeout.Timeout() {
		t.Fatal(err)
	}
}
