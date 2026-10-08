//go:build linux

package net

import (
    stdnet "net"
    "os"
    "path/filepath"
    "strings"
    "testing"
)

func TestDiTunnelIPv6ProcWordsAndInputUnchanged(t *testing.T) {
    ip := stdnet.ParseIP("2001:db8:77::1234")
    before := append([]byte(nil), ip...)
    result, err := formatLittleEndianString(IPAddress(ip), 443)
    if err != nil || result != "B80D0120000077000000000034120000:01BB" { t.Fatalf("proc words: %s %v", result, err) }
    if !ip.Equal(before) { t.Fatal("routing address modified") }
}

func TestDiTunnelWildcardUdpAndAmbiguity(t *testing.T) {
    path := filepath.Join(t.TempDir(), "udp")
    row := func(addr, inode string) string { return "0: " + addr + " 00000000:0000 07 0 0 0 0 0 " + inode + "\n" }
    if err := os.WriteFile(path, []byte(row("00000000:1234","17")),0600); err != nil { t.Fatal(err) }
    if inode,err := findInodeInFile(path,"0100007F:1234"); err != nil || inode != "17" { t.Fatalf("wildcard: %s %v",inode,err) }
    _ = os.WriteFile(path, []byte(row("00000000:1234","17") + row("00000000:1234","18")),0600)
    if _,err := findInodeInFile(path,"0100007F:1234"); err == nil { t.Fatal("ambiguous owner accepted") }
    _ = os.WriteFile(path, []byte(row("00000000:1234","17") + row("0100007F:1234","19")),0600)
    if inode,err := findInodeInFile(path,"0100007F:1234"); err != nil || inode != "19" { t.Fatalf("exact owner: %s %v",inode,err) }
}

func TestDiTunnelRealLocalProcessLookup(t *testing.T) {
    for _,network := range []string{"tcp4","tcp6","udp4","udp6","udp"} {
        t.Run(network,func(t *testing.T) {
            host, bind := "127.0.0.1", "0.0.0.0:0"
            if strings.HasSuffix(network,"6") || network=="udp" { host,bind = "::1","[::]:0" }
            var port int
            if strings.HasPrefix(network,"tcp") {
                listener,err := stdnet.Listen(network,stdnet.JoinHostPort(host,"0")); if err != nil { t.Fatal(err) }; defer listener.Close()
                port = listener.Addr().(*stdnet.TCPAddr).Port
            } else {
                listener,err := stdnet.ListenPacket(network,bind); if err != nil { t.Fatal(err) }; defer listener.Close()
                port = listener.LocalAddr().(*stdnet.UDPAddr).Port
            }
            destination := Destination{Network: Network_UDP, Address: IPAddress(stdnet.ParseIP(host)), Port: Port(port)}
            if strings.HasPrefix(network,"tcp") { destination.Network=Network_TCP }
            pid,_,path,err := FindProcess(destination)
            if err != nil || pid != os.Getpid() || path == "" { t.Fatalf("actual socket owner: %d %s %v",pid,path,err) }
            if network=="udp" {
                destination.Address=IPAddress(stdnet.ParseIP("127.0.0.1"))
                pid,_,_,err=FindProcess(destination)
                if err != nil || pid != os.Getpid() { t.Fatalf("dual stack owner: %d %v",pid,err) }
            }
        })
    }
}
