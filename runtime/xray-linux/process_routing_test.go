//go:build linux

package router

import (
    stdnet "net"
    "os"
    "testing"
    "github.com/xtls/xray-core/common/net"
    "github.com/xtls/xray-core/common/session"
    routingsession "github.com/xtls/xray-core/features/routing/session"
)

func TestDiTunnelProcessRoutingBothFamilies(t *testing.T) {
    path,err := os.Executable(); if err != nil { t.Fatal(err) }
    matcher := NewProcessNameMatcher([]string{path})
    for _,network := range []string{"tcp4","tcp6","udp4","udp6"} {
        t.Run(network,func(t *testing.T) {
            host,bind := "127.0.0.1","0.0.0.0:0"
            if network=="tcp6" || network=="udp6" { host,bind="::1","[::]:0" }
            var port int
            source := net.Destination{Network:net.Network_TCP,Address:net.ParseAddress(host)}
            if network=="tcp4" || network=="tcp6" {
                listener,err := stdnet.Listen(network,stdnet.JoinHostPort(host,"0")); if err != nil { t.Fatal(err) }; defer listener.Close()
                port=listener.Addr().(*stdnet.TCPAddr).Port
            } else {
                listener,err := stdnet.ListenPacket(network,bind); if err != nil { t.Fatal(err) }; defer listener.Close()
                port=listener.LocalAddr().(*stdnet.UDPAddr).Port
                source.Network=net.Network_UDP
            }
            source.Port=net.Port(port)
            ctx := &routingsession.Context{Inbound:&session.Inbound{Source:source},Outbound:&session.Outbound{Target:source}}
            if !matcher.Apply(ctx) { t.Fatal("actual process routing failed") }
            if NewProcessNameMatcher([]string{path+"-other"}).Apply(ctx) { t.Fatal("wrong executable accepted") }
        })
    }
}
