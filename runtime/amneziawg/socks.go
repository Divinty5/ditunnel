package main

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/binary"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"sync"
	"time"
)

type tunnelDialer interface {
	DialContext(context.Context, string, string) (net.Conn, error)
}
type socksServer struct {
	network            tunnelDialer
	username, password string
	listener           *net.TCPListener
	slots              chan struct{}
	mu                 sync.Mutex
	connections        map[net.Conn]struct{}
	closing            bool
}

func newSOCKSServer(network tunnelDialer, username, password string) (*socksServer, error) {
	listener, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return nil, err
	}
	return &socksServer{network: network, username: username, password: password, listener: listener, slots: make(chan struct{}, 1024), connections: make(map[net.Conn]struct{})}, nil
}
func (s *socksServer) port() int { return s.listener.Addr().(*net.TCPAddr).Port }
func (s *socksServer) close() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closing = true
	s.listener.Close()
	for connection := range s.connections {
		connection.Close()
	}
}
func (s *socksServer) serve(ctx context.Context) error {
	stop := context.AfterFunc(ctx, s.close)
	defer stop()
	for {
		connection, err := s.listener.AcceptTCP()
		if err != nil {
			return err
		}
		select {
		case s.slots <- struct{}{}:
		default:
			connection.Close()
			continue
		}
		s.mu.Lock()
		if s.closing {
			s.mu.Unlock()
			connection.Close()
			<-s.slots
			return net.ErrClosed
		}
		s.connections[connection] = struct{}{}
		s.mu.Unlock()
		go func() {
			defer func() { connection.Close(); s.mu.Lock(); delete(s.connections, connection); s.mu.Unlock(); <-s.slots }()
			_ = s.handle(ctx, connection)
		}()
	}
}

func readByte(r io.Reader) (byte, error) {
	var b [1]byte
	_, err := io.ReadFull(r, b[:])
	return b[0], err
}
func readText(r io.Reader) (string, error) {
	length, err := readByte(r)
	if err != nil || length == 0 {
		return "", errors.New("invalid string")
	}
	value := make([]byte, int(length))
	_, err = io.ReadFull(r, value)
	return string(value), err
}
func readAddress(r io.Reader) (string, error) {
	kind, err := readByte(r)
	if err != nil {
		return "", err
	}
	var host string
	switch kind {
	case 1, 4:
		length := 4
		if kind == 4 {
			length = 16
		}
		value := make([]byte, length)
		if _, err := io.ReadFull(r, value); err != nil {
			return "", err
		}
		address, _ := netip.AddrFromSlice(value)
		host = address.String()
	case 3:
		host, err = readText(r)
		if err != nil {
			return "", err
		}
		if bytes.IndexByte([]byte(host), 0) >= 0 {
			return "", errors.New("invalid host")
		}
	default:
		return "", errors.New("invalid address type")
	}
	var port [2]byte
	if _, err := io.ReadFull(r, port[:]); err != nil {
		return "", err
	}
	return net.JoinHostPort(host, strconv.Itoa(int(binary.BigEndian.Uint16(port[:])))), nil
}
func encodeAddress(address netip.AddrPort) []byte {
	kind := byte(1)
	if address.Addr().Is6() {
		kind = 4
	}
	result := append([]byte{kind}, address.Addr().AsSlice()...)
	return binary.BigEndian.AppendUint16(result, address.Port())
}
func reply(connection net.Conn, code byte, address netip.AddrPort) error {
	_, err := connection.Write(append([]byte{5, code, 0}, encodeAddress(address)...))
	return err
}

func (s *socksServer) handle(ctx context.Context, connection *net.TCPConn) error {
	connection.SetDeadline(time.Now().Add(10 * time.Second))
	var greeting [2]byte
	if _, err := io.ReadFull(connection, greeting[:]); err != nil || greeting[0] != 5 || greeting[1] == 0 {
		return errors.New("invalid greeting")
	}
	methods := make([]byte, int(greeting[1]))
	if _, err := io.ReadFull(connection, methods); err != nil {
		return err
	}
	if !bytes.Contains(methods, []byte{2}) {
		connection.Write([]byte{5, 255})
		return errors.New("authentication required")
	}
	if _, err := connection.Write([]byte{5, 2}); err != nil {
		return err
	}
	version, err := readByte(connection)
	if err != nil || version != 1 {
		return errors.New("invalid authentication")
	}
	username, err := readText(connection)
	if err != nil {
		return err
	}
	password, err := readText(connection)
	if err != nil {
		return err
	}
	userOK := subtle.ConstantTimeCompare([]byte(username), []byte(s.username))
	passOK := subtle.ConstantTimeCompare([]byte(password), []byte(s.password))
	if userOK&passOK != 1 {
		connection.Write([]byte{1, 1})
		return errors.New("authentication failed")
	}
	if _, err := connection.Write([]byte{1, 0}); err != nil {
		return err
	}
	var request [3]byte
	if _, err := io.ReadFull(connection, request[:]); err != nil || request[0] != 5 || request[2] != 0 {
		return errors.New("invalid request")
	}
	destination, err := readAddress(connection)
	if err != nil {
		return err
	}
	empty := netip.MustParseAddrPort("127.0.0.1:0")
	switch request[1] {
	case 1:
		dialCtx, cancel := context.WithTimeout(ctx, 12*time.Second)
		defer cancel()
		remote, err := s.network.DialContext(dialCtx, "tcp", destination)
		if err != nil {
			reply(connection, 4, empty)
			return err
		}
		defer remote.Close()
		stop := context.AfterFunc(ctx, func() { remote.Close(); connection.Close() })
		defer stop()
		if err := reply(connection, 0, empty); err != nil {
			return err
		}
		connection.SetDeadline(time.Time{})
		done := make(chan struct{})
		go func() {
			io.Copy(remote, connection)
			if half, ok := remote.(interface{ CloseWrite() error }); ok {
				half.CloseWrite()
			} else {
				remote.Close()
			}
			close(done)
		}()
		io.Copy(connection, remote)
		connection.CloseWrite()
		connection.CloseRead()
		remote.Close()
		<-done
		return nil
	case 3:
		connection.SetDeadline(time.Time{})
		return s.associateUDP(ctx, connection, destination)
	default:
		reply(connection, 7, empty)
		return errors.New("unsupported command")
	}
}

func parseUDP(packet []byte) (string, []byte, error) {
	if len(packet) < 4 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 {
		return "", nil, errors.New("fragmented/invalid datagram")
	}
	reader := bytes.NewReader(packet[3:])
	address, err := readAddress(reader)
	if err != nil {
		return "", nil, err
	}
	host, port, err := net.SplitHostPort(address)
	if err != nil || port == "0" || host == "0.0.0.0" || host == "::" {
		return "", nil, errors.New("invalid datagram destination")
	}
	return address, packet[len(packet)-reader.Len():], nil
}

func (s *socksServer) associateUDP(parent context.Context, control *net.TCPConn, announced string) error {
	expected, err := netip.ParseAddrPort(announced)
	if err != nil || (!expected.Addr().IsUnspecified() && !expected.Addr().IsLoopback()) {
		return errors.New("local association required")
	}
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		return err
	}
	defer socket.Close()
	if err := reply(control, 0, socket.LocalAddr().(*net.UDPAddr).AddrPort()); err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	stop := context.AfterFunc(ctx, func() { socket.Close(); control.Close() })
	defer stop()
	go func() { io.Copy(io.Discard, control); cancel() }()
	flows := make(map[string]net.Conn)
	var flowMu sync.Mutex
	defer func() {
		flowMu.Lock()
		defer flowMu.Unlock()
		for _, flow := range flows {
			flow.Close()
		}
	}()
	var client netip.AddrPort
	packet := make([]byte, 65535)
	for {
		n, source, err := socket.ReadFromUDPAddrPort(packet)
		if err != nil {
			return err
		}
		if !source.Addr().IsLoopback() || (expected.Port() != 0 && source.Port() != expected.Port()) || (client.IsValid() && source != client) {
			continue
		}
		destination, payload, err := parseUDP(packet[:n])
		if err != nil {
			continue
		}
		if !client.IsValid() {
			client = source
		}
		flowMu.Lock()
		remote := flows[destination]
		count := len(flows)
		flowMu.Unlock()
		if remote == nil {
			if count >= 128 {
				continue
			}
			dialCtx, dialCancel := context.WithTimeout(ctx, 5*time.Second)
			remote, err = s.network.DialContext(dialCtx, "udp", destination)
			dialCancel()
			if err != nil {
				continue
			}
			target, parseErr := netip.ParseAddrPort(remote.RemoteAddr().String())
			if parseErr != nil {
				remote.Close()
				continue
			}
			remote.SetDeadline(time.Now().Add(90 * time.Second))
			flowMu.Lock()
			flows[destination] = remote
			flowMu.Unlock()
			go func(key string, flow net.Conn, target, receiver netip.AddrPort) {
				defer func() {
					flow.Close()
					flowMu.Lock()
					if flows[key] == flow {
						delete(flows, key)
					}
					flowMu.Unlock()
				}()
				buffer := make([]byte, 65535)
				header := append([]byte{0, 0, 0}, encodeAddress(target)...)
				for {
					n, err := flow.Read(buffer)
					if err != nil {
						return
					}
					flow.SetDeadline(time.Now().Add(90 * time.Second))
					if _, err := socket.WriteToUDPAddrPort(append(header, buffer[:n]...), receiver); err != nil {
						return
					}
				}
			}(destination, remote, target, client)
		}
		remote.SetDeadline(time.Now().Add(90 * time.Second))
		if _, err := remote.Write(payload); err != nil {
			remote.Close()
		}
	}
}
