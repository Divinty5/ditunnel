package main

import (
	"bytes"
	"context"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strings"
	"testing"
	"time"

	"golang.org/x/crypto/curve25519"
)

const testUser = "0123456789abcdef0123456789abcdef"
const testPassword = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

func TestInterfaceBindingOnLoopback(t *testing.T) {
	interfaces, err := net.Interfaces()
	if err != nil {
		t.Fatal(err)
	}
	for _, adapter := range interfaces {
		if adapter.Flags&net.FlagLoopback == 0 {
			continue
		}
		socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
		if err != nil {
			t.Fatal(err)
		}
		defer socket.Close()
		if err := bindInterface(socket, uint32(adapter.Index)); err != nil {
			t.Fatal(err)
		}
		if _, err := socket.WriteToUDP([]byte("test"), socket.LocalAddr().(*net.UDPAddr)); err != nil {
			t.Fatal(err)
		}
		socket.SetReadDeadline(time.Now().Add(time.Second))
		var buffer [4]byte
		if _, _, err := socket.ReadFromUDP(buffer[:]); err != nil {
			t.Fatal(err)
		}
		if string(buffer[:]) != "test" {
			t.Fatal("loopback payload mismatch")
		}
		return
	}
	t.Fatal("loopback interface not found")
}

func testConfig(uapi, address string) configuration {
	v6 := "2001:db8::2"
	if address == "192.0.2.1" {
		v6 = "2001:db8::1"
	}
	return configuration{UAPI: uapi, Addresses: []string{address, v6}, DNS: []string{"192.0.2.2"}, MTU: 1280, Username: testUser, Password: testPassword, SourceAddress: "127.0.0.1"}
}
func testKeys(t *testing.T, value byte) (string, string) {
	t.Helper()
	private := bytes.Repeat([]byte{value}, 32)
	public, err := curve25519.X25519(private, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	return hex.EncodeToString(private), hex.EncodeToString(public)
}
func authClient(t *testing.T, port int) net.Conn {
	t.Helper()
	connection, err := net.DialTimeout("tcp4", fmt.Sprintf("127.0.0.1:%d", port), 3*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { connection.Close() })
	connection.SetDeadline(time.Now().Add(8 * time.Second))
	connection.Write([]byte{5, 1, 2})
	var response [2]byte
	if _, err := io.ReadFull(connection, response[:]); err != nil || response != [2]byte{5, 2} {
		t.Fatalf("authentication negotiation: %v %v", response, err)
	}
	request := append([]byte{1, byte(len(testUser))}, testUser...)
	request = append(request, byte(len(testPassword)))
	request = append(request, testPassword...)
	connection.Write(request)
	if _, err := io.ReadFull(connection, response[:]); err != nil || response != [2]byte{1, 0} {
		t.Fatalf("authentication: %v %v", response, err)
	}
	return connection
}
func request(t *testing.T, connection net.Conn, command byte, target netip.AddrPort) netip.AddrPort {
	t.Helper()
	connection.Write(append([]byte{5, command, 0}, encodeAddress(target)...))
	var header [3]byte
	if _, err := io.ReadFull(connection, header[:]); err != nil || header != [3]byte{5, 0, 0} {
		t.Fatalf("request failed: %v %v", header, err)
	}
	address, err := readAddress(connection)
	if err != nil {
		t.Fatal(err)
	}
	result, err := netip.ParseAddrPort(address)
	if err != nil {
		t.Fatal(err)
	}
	return result
}

// Two real AWG devices use UDP on loopback and in-memory TUNs. No OS routes,
// DNS settings, adapters, services or firewall rules are touched.
func TestEncryptedTCPAndUDP(t *testing.T) {
	variants := map[string]string{
		"AWG1": "jc=2\njmin=10\njmax=20\ns1=16\ns2=32\nh1=12345\nh2=23456\nh3=34567\nh4=45678\n",
		"AWG2": "jc=2\njmin=10\njmax=20\ns1=16\ns2=32\ns3=16\ns4=16\nh1=12345-12355\nh2=23456-23466\nh3=34567-34577\nh4=45678-45688\n",
		"AWG3": "s1=16\ns2=32\ns3=16\ns4=16\nh1=12345\nh2=23456\nh3=34567\nh4=45678\nheader_protection_key=" + strings.Repeat("03", 32) + "\ncontent_padding_addition=10-20\n",
	}
	for name, obfuscation := range variants {
		t.Run(name, func(t *testing.T) {
			private1, public1 := testKeys(t, 1)
			private2, public2 := testKeys(t, 2)
			server, serverNet, err := createDevice(testConfig("private_key="+private2+"\n"+obfuscation+"public_key="+public1+"\nallowed_ip=192.0.2.1/32\nallowed_ip=2001:db8::1/128\n", "192.0.2.2"), false)
			if err != nil {
				t.Fatal(err)
			}
			defer server.Close()
			if err := server.Up(); err != nil {
				t.Fatal(err)
			}
			state, err := server.IpcGet()
			if err != nil {
				t.Fatal(err)
			}
			var port string
			for _, line := range strings.Split(state, "\n") {
				if strings.HasPrefix(line, "listen_port=") {
					port = strings.TrimPrefix(line, "listen_port=")
				}
			}
			if port == "" || port == "0" {
				t.Fatal("server transport is not ready")
			}
			client, clientNet, err := createDevice(testConfig("private_key="+private1+"\n"+obfuscation+"public_key="+public2+"\nendpoint=127.0.0.1:"+port+"\nallowed_ip=192.0.2.2/32\nallowed_ip=2001:db8::2/128\n", "192.0.2.1"), false)
			if err != nil {
				t.Fatal(err)
			}
			defer client.Close()
			if err := client.Up(); err != nil {
				t.Fatal(err)
			}
			t.Run("TunnelDNS", func(t *testing.T) { checkTunnelDNS(t, clientNet, serverNet) })
			for _, host := range []string{"192.0.2.2", "2001:db8::2"} {
				tcpTarget := netip.MustParseAddrPort(net.JoinHostPort(host, "9001"))
				udpTarget := netip.MustParseAddrPort(net.JoinHostPort(host, "9002"))
				tcp, err := serverNet.ListenTCPAddrPort(tcpTarget)
				if err != nil {
					t.Fatal(err)
				}
				defer tcp.Close()
				go func() {
					c, err := tcp.Accept()
					if err == nil {
						defer c.Close()
						io.Copy(c, c)
					}
				}()
				udp, err := serverNet.ListenUDPAddrPort(udpTarget)
				if err != nil {
					t.Fatal(err)
				}
				defer udp.Close()
				go func() {
					buffer := make([]byte, 2048)
					n, source, err := udp.ReadFrom(buffer)
					if err == nil {
						udp.WriteTo(buffer[:n], source)
					}
				}()
				socks, err := newSOCKSServer(clientNet, testUser, testPassword)
				if err != nil {
					t.Fatal(err)
				}
				defer socks.close()
				ctx, cancel := context.WithCancel(context.Background())
				defer cancel()
				go socks.serve(ctx)
				openClient := func() net.Conn { return authClient(t, socks.port()) }
				if name == "AWG1" && host == "192.0.2.2" {
					if port, ok := xrayProxy(t, socks); ok {
						openClient = func() net.Conn { return noAuthClient(t, port) }
					}
				}
				connection := openClient()
				request(t, connection, 1, tcpTarget)
				payload := []byte("encrypted TCP through AWG")
				connection.Write(payload)
				received := make([]byte, len(payload))
				if _, err := io.ReadFull(connection, received); err != nil || !bytes.Equal(received, payload) {
					t.Fatalf("TCP echo: %v", err)
				}
				connection.Close()
				control := openClient()
				relay := request(t, control, 3, netip.MustParseAddrPort("0.0.0.0:0"))
				socket, err := net.DialUDP("udp4", nil, net.UDPAddrFromAddrPort(relay))
				if err != nil {
					t.Fatal(err)
				}
				defer socket.Close()
				socket.SetDeadline(time.Now().Add(8 * time.Second))
				payload = []byte("encrypted UDP through AWG")
				frame := append([]byte{0, 0, 0}, encodeAddress(udpTarget)...)
				socket.Write(append(frame, payload...))
				buffer := make([]byte, 2048)
				n, err := socket.Read(buffer)
				if err != nil {
					t.Fatal(err)
				}
				target, echo, err := parseUDP(buffer[:n])
				if err != nil || target != udpTarget.String() || !bytes.Equal(echo, payload) {
					t.Fatalf("UDP echo failed: %v", err)
				}
			}
		})
	}
}

type rejectingDialer struct{}

func (rejectingDialer) DialContext(context.Context, string, string) (net.Conn, error) {
	return nil, fmt.Errorf("tunnel unavailable")
}
func TestSOCKSRequiresAuthenticationAndNeverFallsBack(t *testing.T) {
	socks, err := newSOCKSServer(rejectingDialer{}, testUser, testPassword)
	if err != nil {
		t.Fatal(err)
	}
	defer socks.close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go socks.serve(ctx)
	c, err := net.Dial("tcp4", fmt.Sprintf("127.0.0.1:%d", socks.port()))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(time.Second))
	c.Write([]byte{5, 1, 0})
	response := make([]byte, 2)
	io.ReadFull(c, response)
	if !bytes.Equal(response, []byte{5, 255}) {
		t.Fatal("unauthenticated proxy allowed")
	}
	authenticated := authClient(t, socks.port())
	authenticated.Write(append([]byte{5, 1, 0}, encodeAddress(netip.MustParseAddrPort("192.0.2.99:80"))...))
	reply := make([]byte, 10)
	if _, err := io.ReadFull(authenticated, reply); err != nil || reply[1] != 4 {
		t.Fatal("unavailable tunnel must fail closed")
	}
}
func TestValidationUsesNoTransportSocket(t *testing.T) {
	private, _ := testKeys(t, 1)
	dev, _, err := createDevice(testConfig("private_key="+private+"\njc=2\n", "192.0.2.1"), true)
	if err != nil {
		t.Fatal(err)
	}
	defer dev.Close()
	if err := dev.Up(); err != nil {
		t.Fatal(err)
	}
	state, err := dev.IpcGet()
	if err != nil || strings.Contains(state, "listen_port=") {
		t.Fatal("validation opened a transport port")
	}
}
func TestInvalidObfuscationIsRejectedWithoutSecrets(t *testing.T) {
	private, _ := testKeys(t, 1)
	_, _, err := createDevice(testConfig("private_key="+private+"\ni1=<secret-bad-template>\n", "192.0.2.1"), true)
	if err == nil || strings.Contains(err.Error(), private) || strings.Contains(err.Error(), "secret-bad-template") {
		t.Fatal("invalid configuration or redaction")
	}
}
func TestUDPRejectsFragmentsAndMalformedAddresses(t *testing.T) {
	for _, packet := range [][]byte{nil, {0, 0, 1, 1, 192, 0, 2, 1, 0, 80}, {0, 0, 0, 4, 1}, {0, 0, 0, 3, 0, 0, 80}, {0, 0, 0, 1, 0, 0, 0, 0, 0, 80}} {
		if _, _, err := parseUDP(packet); err == nil {
			t.Fatalf("accepted malformed packet %v", packet)
		}
	}
}
