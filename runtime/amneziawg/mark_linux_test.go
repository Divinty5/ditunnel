//go:build linux && !android

package main

import (
	"errors"
	"net"
	"testing"

	"golang.org/x/sys/unix"
)

func TestMarkAppliedAndCleared(t *testing.T) {
	bind := &udpBind{source: "127.0.0.1"}
	if err := bind.SetMark(42); err != nil {
		t.Fatal(err)
	}
	_, _, err := bind.Open(0)
	if errors.Is(err, unix.EPERM) || errors.Is(err, unix.EACCES) {
		t.Skip("SO_MARK needs CAP_NET_ADMIN or CAP_NET_RAW")
	}
	if err != nil {
		t.Fatal(err)
	}
	defer bind.Close()
	readMark := func(socket *net.UDPConn) int {
		value := -1
		err := controlSocket(socket, func(fd int) error {
			var err error
			value, err = unix.GetsockoptInt(fd, unix.SOL_SOCKET, unix.SO_MARK)
			return err
		})
		if err != nil {
			t.Fatal(err)
		}
		return value
	}
	if readMark(bind.socket) != 42 {
		t.Fatal("mark missing on opened transport")
	}
	if err := bind.SetMark(0); err != nil {
		t.Fatal(err)
	}
	if readMark(bind.socket) != 0 {
		t.Fatal("previous mark was not cleared")
	}
}
