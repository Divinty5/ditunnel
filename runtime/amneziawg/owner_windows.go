package main

import (
	"os"

	"golang.org/x/sys/windows"
)

var terminationSignals = []os.Signal{os.Interrupt}

type ownerWatch struct{ handle windows.Handle }

func watchOwner(pid int) (*ownerWatch, error) {
	handle, err := windows.OpenProcess(windows.SYNCHRONIZE, false, uint32(pid))
	if err != nil {
		return nil, err
	}
	return &ownerWatch{handle}, nil
}
func (w *ownerWatch) exited() bool {
	status, err := windows.WaitForSingleObject(w.handle, 0)
	return err != nil || status == windows.WAIT_OBJECT_0
}
func (w *ownerWatch) close() { windows.CloseHandle(w.handle) }
