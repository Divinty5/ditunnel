package main

import "net"

// Android host binds sockets to its physical Network and calls VpnService.Protect.
func bindInterface(*net.UDPConn, uint32) error { return nil }
