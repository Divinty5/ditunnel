package main

import (
	"errors"
	"net"
	"net/netip"
	"sync"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
)

// The encrypted transport is bound to the physical source address supplied by the
// Windows host. The in-memory TUN never creates an OS adapter or route.
type udpBind struct {
	mu             sync.Mutex
	socket         *net.UDPConn
	source         string
	dryRun         bool
	dryClosed      chan struct{}
	interfaceIndex uint32
}
type udpEndpoint struct{ address netip.AddrPort }

func (e *udpEndpoint) ClearSrc()           {}
func (e *udpEndpoint) SrcToString() string { return "" }
func (e *udpEndpoint) DstToString() string { return e.address.String() }
func (e *udpEndpoint) DstToBytes() []byte  { result, _ := e.address.MarshalBinary(); return result }
func (e *udpEndpoint) DstIP() netip.Addr   { return e.address.Addr() }
func (e *udpEndpoint) SrcIP() netip.Addr   { return netip.Addr{} }
func (b *udpBind) ParseEndpoint(value string) (conn.Endpoint, error) {
	address, err := netip.ParseAddrPort(value)
	if err != nil || !address.Addr().Is4() || address.Port() == 0 {
		return nil, errors.New("IPv4 endpoint required")
	}
	return &udpEndpoint{address}, nil
}
func (b *udpBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.socket != nil || b.dryClosed != nil {
		return nil, 0, conn.ErrBindAlreadyOpen
	}
	if b.dryRun {
		closed := make(chan struct{})
		b.dryClosed = closed
		receive := func([][]byte, []int, []conn.Endpoint) (int, error) { <-closed; return 0, net.ErrClosed }
		return []conn.ReceiveFunc{receive}, port, nil
	}
	source := net.IPv4zero
	if b.source != "" {
		source = net.ParseIP(b.source)
		if source == nil || source.To4() == nil {
			return nil, 0, errors.New("invalid physical source")
		}
	}
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: source, Port: int(port)})
	if err != nil {
		return nil, 0, err
	}
	if err := bindInterface(socket, b.interfaceIndex); err != nil {
		socket.Close()
		return nil, 0, err
	}
	b.socket = socket
	receive := func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
		n, address, err := socket.ReadFromUDPAddrPort(packets[0])
		if err != nil {
			return 0, err
		}
		sizes[0] = n
		eps[0] = &udpEndpoint{address}
		return 1, nil
	}
	return []conn.ReceiveFunc{receive}, uint16(socket.LocalAddr().(*net.UDPAddr).Port), nil
}
func (b *udpBind) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.dryClosed != nil {
		close(b.dryClosed)
		b.dryClosed = nil
	}
	if b.socket == nil {
		return nil
	}
	err := b.socket.Close()
	b.socket = nil
	return err
}
func (b *udpBind) SetMark(uint32) error { return nil }
func (b *udpBind) BatchSize() int       { return 1 }
func (b *udpBind) Send(packets [][]byte, ep conn.Endpoint) error {
	b.mu.Lock()
	socket := b.socket
	dry := b.dryRun
	b.mu.Unlock()
	if dry {
		return nil
	}
	if socket == nil {
		return net.ErrClosed
	}
	destination, ok := ep.(*udpEndpoint)
	if !ok {
		return conn.ErrWrongEndpointType
	}
	for _, packet := range packets {
		if _, err := socket.WriteToUDPAddrPort(packet, destination.address); err != nil {
			return err
		}
	}
	return nil
}
