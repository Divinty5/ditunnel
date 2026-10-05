package main

/*
#include <stdint.h>
*/
import "C"

import (
	"context"
	"encoding/json"
	"net"
	"os"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/device"
)

var androidRuntime struct {
	sync.Mutex
	device *device.Device
	server *socksServer
	cancel context.CancelFunc
}

//export DtAwgStart
func DtAwgStart(configurationJSON *C.char, probeAddress *C.char, protectedFD C.int) C.int {
	androidRuntime.Lock()
	defer androidRuntime.Unlock()
	if androidRuntime.device != nil {
		return -1
	}
	if protectedFD < 0 {
		return -6
	}
	var cfg configuration
	if json.Unmarshal([]byte(C.GoString(configurationJSON)), &cfg) != nil || cfg.MTU < 576 || cfg.MTU > 65535 || len(cfg.Username) < 16 || len(cfg.Password) < 32 {
		return -2
	}
	ipv6 := strings.Contains(cfg.UAPI, "endpoint=[")
	// Android creates and protects the host socket on a managed thread before
	// entering Go. Duplicate it for each bind open; never call Mono from a Go thread.
	bind := &udpBind{ipv6: ipv6, openSocket: func() (*net.UDPConn, error) {
		fd, err := syscall.Dup(int(protectedFD))
		if err != nil {
			return nil, err
		}
		file := os.NewFile(uintptr(fd), "protected-awg")
		defer file.Close()
		packet, err := net.FilePacketConn(file)
		if err != nil {
			return nil, err
		}
		socket, ok := packet.(*net.UDPConn)
		if !ok {
			packet.Close()
			return nil, net.ErrClosed
		}
		return socket, nil
	}}
	dev, network, err := createDeviceWithBind(cfg, bind)
	if err != nil {
		if err.Error() == "AmneziaWG rejected the configuration" {
			return -7
		}
		return -3
	}
	if dev.Up() != nil {
		dev.Close()
		return -4
	}
	server, err := newSOCKSServer(network, cfg.Username, cfg.Password)
	if err != nil {
		dev.Close()
		return -5
	}
	ctx, cancel := context.WithCancel(context.Background())
	androidRuntime.device = dev
	androidRuntime.server = server
	androidRuntime.cancel = cancel
	go func() { _ = server.serve(ctx) }()
	address := C.GoString(probeAddress)
	go func() {
		probeCtx, stop := context.WithTimeout(ctx, 18*time.Second)
		defer stop()
		connection, err := network.DialContext(probeCtx, "udp", net.JoinHostPort(address, "9"))
		if err == nil {
			defer connection.Close()
			_ = connection.SetDeadline(time.Now().Add(18 * time.Second))
			_, _ = connection.Write([]byte{0})
		}
	}()
	return C.int(server.port())
}

//export DtAwgHasHandshake
func DtAwgHasHandshake() C.int {
	androidRuntime.Lock()
	defer androidRuntime.Unlock()
	if androidRuntime.device == nil {
		return 0
	}
	value, err := androidRuntime.device.IpcGet()
	if err != nil {
		return 0
	}
	for _, line := range strings.Split(value, "\n") {
		if strings.HasPrefix(line, "last_handshake_time_sec=") && strings.TrimPrefix(line, "last_handshake_time_sec=") != "0" {
			return 1
		}
	}
	return 0
}

//export DtAwgStop
func DtAwgStop() {
	androidRuntime.Lock()
	defer androidRuntime.Unlock()
	if androidRuntime.cancel != nil {
		androidRuntime.cancel()
	}
	if androidRuntime.server != nil {
		androidRuntime.server.close()
	}
	if androidRuntime.device != nil {
		androidRuntime.device.Close()
	}
	androidRuntime.cancel = nil
	androidRuntime.server = nil
	androidRuntime.device = nil
}

func main() {}
