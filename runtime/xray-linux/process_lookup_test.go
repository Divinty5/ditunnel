//go:build linux

package net

import (
    stdnet "net"
    "os"
    "path/filepath"
    "strings"
    "testing"
)

func TestDiTunnelFlatpakPathRequiresMatchingExecutableInode(t *testing.T) {
    root := t.TempDir()
    proc := filepath.Join(root, "proc")
    host := filepath.Join(root, "app-files")
    if err := os.MkdirAll(filepath.Join(proc, "root"), 0700); err != nil { t.Fatal(err) }
    if err := os.MkdirAll(filepath.Join(host, "bin"), 0700); err != nil { t.Fatal(err) }
    executable := filepath.Join(host, "bin", "client")
    if err := os.WriteFile(executable, []byte("synthetic ELF"), 0700); err != nil { t.Fatal(err) }
    if err := os.Symlink(executable, filepath.Join(proc, "exe")); err != nil { t.Fatal(err) }
    metadata := filepath.Join(proc, "root", ".flatpak-info")
    write := func(value string) { t.Helper(); if err := os.WriteFile(metadata, []byte(value), 0600); err != nil { t.Fatal(err) } }
    write("[Application]\nname=org.example.Client\n[Instance]\napp-path=" + host + "\n")
    if got := resolveFlatpakExecutable("/app/bin/client", proc); got != executable { t.Fatalf("host mapping: %s", got) }
    if got := resolveFlatpakExecutable("/usr/bin/client", proc); got != "/usr/bin/client" { t.Fatal("non-app executable remapped") }
    // A different app's executable must not inherit this app's routing rule.
    other := filepath.Join(host, "bin", "other")
    if err := os.WriteFile(other, []byte("other ELF"), 0700); err != nil { t.Fatal(err) }
    if got := resolveFlatpakExecutable("/app/bin/other", proc); got != "/app/bin/other" { t.Fatal("different inode accepted") }
    write("[Instance]\napp-path=relative\n")
    if got := resolveFlatpakExecutable("/app/bin/client", proc); got != "/app/bin/client" { t.Fatal("relative host path accepted") }
    write("[Instance]\napp-path=" + host + "\n" + strings.Repeat("#", 65536))
    if got := resolveFlatpakExecutable("/app/bin/client", proc); got != "/app/bin/client" { t.Fatal("oversized metadata accepted") }
}

func TestDiTunnelLiveFlatpakMountNamespace(t *testing.T) {
    pid, expected := os.Getenv("DITUNNEL_TEST_FLATPAK_PID"), os.Getenv("DITUNNEL_TEST_FLATPAK_EXE")
    if pid == "" || expected == "" { t.Skip("requires the isolated VM mount-namespace fixture") }
    raw, err := os.Readlink("/proc/" + pid + "/exe")
    if err != nil || !strings.HasPrefix(raw, "/app/") { t.Fatalf("fixture is not a sandbox executable: %s %v", raw, err) }
    actual, err := getAbsPath(pid)
    if err != nil || actual != expected { t.Fatalf("live sandbox mapping: %s %v; expected %s", actual, err, expected) }
}

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
