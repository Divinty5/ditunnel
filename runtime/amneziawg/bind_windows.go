package main

import (
	"golang.org/x/sys/windows"
	"math/bits"
	"net"
)

// Windows SDK IP_UNICAST_IF; x/sys/windows does not expose this option.
const ipUnicastInterface = 31

// IP_UNICAST_IF pins encrypted packets to the physical interface even when a
// tunnel DNS resolver shares the endpoint IP and owns a competing /32 route.
func bindInterface(socket *net.UDPConn, index uint32) error {
	if index == 0 {
		return nil
	}
	raw, err := socket.SyscallConn()
	if err != nil {
		return err
	}
	var optionError error
	if err := raw.Control(func(fd uintptr) {
		optionError = windows.SetsockoptInt(windows.Handle(fd), windows.IPPROTO_IP, ipUnicastInterface, int(bits.ReverseBytes32(index)))
	}); err != nil {
		return err
	}
	return optionError
}
