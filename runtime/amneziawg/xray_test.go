package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"
)

// Exercise the same Xray SOCKS outbound used by the Windows backend without a
// Windows TUN inbound. The only OS connections here are loopback connections.
func xrayProxy(t *testing.T, bridge *socksServer) (int, bool) {
	t.Helper()
	executable := os.Getenv("DITUNNEL_XRAY")
	if executable == "" {
		executable = filepath.Join("..", "..", ".tools", "xray", "26.3.27", "windows-x64", "xray.exe")
	}
	executable, err := filepath.Abs(executable)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(executable); os.IsNotExist(err) {
		t.Log("Xray composition check skipped: run Install-Xray.ps1 to enable it")
		return 0, false
	}
	reservation, err := net.ListenTCP("tcp4", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	port := reservation.Addr().(*net.TCPAddr).Port
	reservation.Close()
	config := map[string]any{
		"log":       map[string]any{"loglevel": "none"},
		"inbounds":  []any{map[string]any{"protocol": "socks", "listen": "127.0.0.1", "port": port, "settings": map[string]any{"auth": "noauth", "udp": true}}},
		"outbounds": []any{map[string]any{"protocol": "socks", "settings": map[string]any{"servers": []any{map[string]any{"address": "127.0.0.1", "port": bridge.port(), "users": []any{map[string]any{"user": testUser, "pass": testPassword}}}}}}},
	}
	content, err := json.Marshal(config)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "xray.json")
	if err := os.WriteFile(path, content, 0600); err != nil {
		t.Fatal(err)
	}
	command := exec.Command(executable, "run", "-config", path)
	command.Stdout = io.Discard
	command.Stderr = io.Discard
	if err := command.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { command.Process.Kill(); command.Wait() })
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		c, err := net.DialTimeout("tcp4", fmt.Sprintf("127.0.0.1:%d", port), 100*time.Millisecond)
		if err == nil {
			c.Close()
			return port, true
		}
		time.Sleep(25 * time.Millisecond)
	}
	t.Fatal("Xray composition listener did not start")
	return 0, false
}

func noAuthClient(t *testing.T, port int) net.Conn {
	t.Helper()
	connection, err := net.DialTimeout("tcp4", fmt.Sprintf("127.0.0.1:%d", port), 3*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { connection.Close() })
	connection.SetDeadline(time.Now().Add(8 * time.Second))
	connection.Write([]byte{5, 1, 0})
	var response [2]byte
	if _, err := io.ReadFull(connection, response[:]); err != nil || response != [2]byte{5, 0} {
		t.Fatalf("Xray negotiation: %v %v", response, err)
	}
	return connection
}
