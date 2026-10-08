//go:build windows || android

package main

import "net"

// Windows uses an interface index; Android uses VpnService.Protect.
func setSocketMark(*net.UDPConn, uint32) error { return nil }
