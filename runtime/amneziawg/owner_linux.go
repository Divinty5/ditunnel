//go:build linux && !android

package main

import (
	"os"
	"syscall"

	"golang.org/x/sys/unix"
)

var terminationSignals = []os.Signal{os.Interrupt, syscall.SIGTERM}

// A pidfd stays attached to the original process even after its PID is reused.
type ownerWatch struct{ fd int }

func watchOwner(pid int) (*ownerWatch, error) {
	fd, err := unix.PidfdOpen(pid, 0)
	if err != nil {
		return nil, err
	}
	return &ownerWatch{fd}, nil
}

func (w *ownerWatch) exited() bool {
	fds := []unix.PollFd{{Fd: int32(w.fd), Events: unix.POLLIN}}
	_, err := unix.Poll(fds, 0)
	return err != nil || fds[0].Revents != 0
}

func (w *ownerWatch) close() { unix.Close(w.fd) }
