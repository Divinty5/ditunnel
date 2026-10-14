"""Apply the bounded Linux process lookup fix to the pinned, verified Xray source."""
import hashlib
import re
import shutil
from pathlib import Path

FORMAT = r'''func formatLittleEndianString(addr Address, port Port) (string, error) {
    ip := addr.IP()
    raw := ip.To16()
    if addr.Family() == AddressFamilyIPv4 { raw = ip.To4() }
    if raw == nil { return "", errors.New("invalid process address") }
    // /proc stores each 32-bit word in native byte order, including IPv6.
    // Work on a copy: routing metadata must never be modified by lookup.
    words := append([]byte(nil), raw...)
    for i := 0; i < len(words); i += 4 {
        words[i], words[i+3] = words[i+3], words[i]
        words[i+1], words[i+2] = words[i+2], words[i+1]
    }
    return strings.ToUpper(hex.EncodeToString(words)) + fmt.Sprintf(":%04X", uint16(port)), nil
}
'''
INODE = r'''func findInodeInFile(filePath, targetHexAddr string) (string, error) {
    file, err := os.Open(filePath)
    if err != nil { return "", err }
    defer file.Close()
    separator := strings.LastIndexByte(targetHexAddr, ':')
    if separator < 0 { return "", errors.New("invalid process socket address") }
    wildcard := strings.Repeat("0", separator) + targetHexAddr[separator:]
    udp := strings.HasSuffix(filePath, "/udp") || strings.HasSuffix(filePath, "/udp6")
    exact, fallback := "", ""
    exactAmbiguous, fallbackAmbiguous := false, false
    scanner := bufio.NewScanner(file)
    for scanner.Scan() {
        fields := strings.Fields(scanner.Text())
        if len(fields) < 10 || fields[9] == "0" { continue }
        if fields[1] == targetHexAddr {
            if exact != "" && exact != fields[9] { exactAmbiguous = true }
            exact = fields[9]
        } else if udp && fields[1] == wildcard {
            // sendto() sockets are usually bound to an unspecified local IP.
            // Never choose an arbitrary owner from a SO_REUSEPORT group.
            if fallback != "" && fallback != fields[9] { fallbackAmbiguous = true }
            fallback = fields[9]
        }
    }
    if err := scanner.Err(); err != nil { return "", err }
    if exactAmbiguous { return "", errors.New("ambiguous process socket") }
    if exact != "" { return exact, nil }
    if fallbackAmbiguous { return "", errors.New("ambiguous wildcard process socket") }
    return fallback, nil
}
'''

FLATPAK = r'''func getAbsPath(pid string) (string, error) {
    processDir := "/proc/" + pid
    executable, err := os.Readlink(processDir + "/exe")
    if err != nil { return "", err }
    return resolveFlatpakExecutable(executable, processDir), nil
}

func resolveFlatpakExecutable(executable, processDir string) string {
    if !strings.HasPrefix(executable, "/app/") || filepath.Clean(executable) != executable { return executable }
    file, err := os.OpenFile(processDir + "/root/.flatpak-info", os.O_RDONLY|syscall.O_NONBLOCK, 0)
    if err != nil { return executable }
    defer file.Close()
    info, err := file.Stat()
    if err != nil || !info.Mode().IsRegular() || info.Size() > 65536 { return executable }
    scanner := bufio.NewScanner(io.LimitReader(file, 65537))
    instance, appPath := false, ""
    for scanner.Scan() {
        line := strings.TrimSpace(scanner.Text())
        if strings.HasPrefix(line, "[") { instance = line == "[Instance]"; continue }
        if instance && strings.HasPrefix(line, "app-path=") { appPath = strings.TrimPrefix(line, "app-path=") }
    }
    if scanner.Err() != nil || !filepath.IsAbs(appPath) { return executable }
    processFile, err := os.Stat(processDir + "/exe")
    if err != nil { return executable }
    candidate := filepath.Join(appPath, strings.TrimPrefix(executable, "/app/"))
    // ProtectHome hides user Flatpak installations from the service namespace.
    // O_PATH follows the host's mount namespace without opening file contents.
    const openPath = 0x200000 // Linux O_PATH, asm-generic/fcntl.h
    for _, path := range []string{candidate, "/proc/1/root" + candidate} {
        host, err := os.OpenFile(path, openPath|syscall.O_CLOEXEC, 0)
        if err != nil { continue }
        hostFile, statErr := host.Stat()
        canonical, pathErr := os.Readlink(fmt.Sprintf("/proc/self/fd/%d", host.Fd()))
        host.Close()
    // Metadata alone is insufficient: the mapped host file must be the very
    // same inode as the running process, not a shared Flatpak launcher.
        if statErr == nil && pathErr == nil && filepath.IsAbs(canonical) && os.SameFile(hostFile, processFile) { return canonical }
    }
    return executable
}
'''

def apply(source, specification):
    source = Path(source)
    path = source / 'common/net/find_process_linux.go'
    if hashlib.sha256(path.read_bytes()).hexdigest() != specification['processSourceSha256']:
        raise RuntimeError('Pinned Xray process source changed; review required')
    text = path.read_text()
    text = text.replace('"os"', '"os"\n"io"\n"path/filepath"\n"syscall"')
    for name, body in [('formatLittleEndianString', FORMAT), ('findInodeInFile', INODE), ('getAbsPath', FLATPAK)]:
        text, count = re.subn(r'func '+name+r'\([^\n]*\n.*?\n}\n', lambda _: body, text, flags=re.S)
        if count != 1: raise RuntimeError('Unexpected pinned Xray function layout')
    marker = '\tinode, err := findInodeInFile(procFile, targetHexAddr)\n'
    if text.count(marker) != 1: raise RuntimeError('Unexpected Xray lookup layout')
    text = text.replace(marker, marker + '''
    if err == nil && inode == "" && dest.Address.Family() == AddressFamilyIPv4 {
        // A dual-stack IPv6 socket can also own IPv4 application packets.
        inode, err = findInodeInFile(procFile + "6", "0000000000000000FFFF0000" + targetHexAddr)
    }
''')
    path.chmod(0o644)
    path.write_text(text, newline='\n')
    shutil.copyfile(Path(__file__).with_name('process_lookup_test.go'), source/'common/net/ditunnel_process_linux_test.go')
    router = source / 'app/router/condition.go'
    if hashlib.sha256(router.read_bytes()).hexdigest() != specification['routerSourceSha256']:
        raise RuntimeError('Pinned Xray router source changed; review required')
    old = 'net.ParseDestination(strings.Join([]string{network, srcIP, srcPort}, ":"))'
    text = router.read_text()
    if text.count(old) != 1: raise RuntimeError('Unexpected Xray process matcher layout')
    router.chmod(0o644)
    router.write_text(text.replace(old, 'net.ParseDestination(network + ":" + net.JoinHostPort(srcIP, srcPort))'), newline='\n')
    shutil.copyfile(Path(__file__).with_name('process_routing_test.go'), source/'app/router/ditunnel_process_linux_test.go')
