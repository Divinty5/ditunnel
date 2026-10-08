//go:build linux && !android

package main

import (
	"net"

	"golang.org/x/sys/unix"
)

func bindInterface(socket *net.UDPConn, index uint32) error {
	if index == 0 {
		return nil
	}
	adapter, err := net.InterfaceByIndex(int(index))
	if err != nil {
		return err
	}
	return controlSocket(socket, func(fd int) error {
		return unix.SetsockoptString(fd, unix.SOL_SOCKET, unix.SO_BINDTODEVICE, adapter.Name)
	})
}

func setSocketMark(socket *net.UDPConn, mark uint32) error {
	return controlSocket(socket, func(fd int) error {
		return unix.SetsockoptInt(fd, unix.SOL_SOCKET, unix.SO_MARK, int(mark))
	})
}

func controlSocket(socket *net.UDPConn, action func(int) error) error {
	raw, err := socket.SyscallConn()
	if err != nil {
		return err
	}
	var socketError error
	if err := raw.Control(func(fd uintptr) { socketError = action(int(fd)) }); err != nil {
		return err
	}
	return socketError
}
