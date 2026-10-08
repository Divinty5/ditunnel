//go:build linux && !android

package main

import (
	"os/exec"
	"testing"
	"time"
)

func TestOwnerWatchTracksExit(t *testing.T) {
	process := exec.Command("/bin/sleep", "30")
	if err := process.Start(); err != nil {
		t.Fatal(err)
	}
	defer process.Process.Kill()
	watch, err := watchOwner(process.Process.Pid)
	if err != nil {
		t.Fatal(err)
	}
	defer watch.close()
	if watch.exited() {
		t.Fatal("running owner reported exited")
	}
	process.Process.Kill()
	process.Wait()
	deadline := time.Now().Add(time.Second)
	for !watch.exited() && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	if !watch.exited() {
		t.Fatal("owner exit not detected")
	}
}

func TestOwnerWatchRejectsMissingProcess(t *testing.T) {
	if watch, err := watchOwner(1 << 30); err == nil {
		watch.close()
		t.Fatal("missing owner accepted")
	}
}
