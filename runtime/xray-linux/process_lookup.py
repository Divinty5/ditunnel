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

def apply(source, specification):
    source = Path(source)
    path = source / 'common/net/find_process_linux.go'
    if hashlib.sha256(path.read_bytes()).hexdigest() != specification['processSourceSha256']:
        raise RuntimeError('Pinned Xray process source changed; review required')
    text = path.read_text()
    for name, body in [('formatLittleEndianString', FORMAT), ('findInodeInFile', INODE)]:
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
