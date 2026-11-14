#!/usr/bin/env bash
set -euo pipefail
repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"
if [[ "$EUID" != 0 ]]; then echo 'Run this isolated network test as root after building DiTunnel.Linux.Smoke.' >&2; exit 1; fi
if [[ "${1:-}" != --isolated ]]; then exec unshare --net -- bash "$0" --isolated; fi
if [[ "$(readlink /proc/self/ns/net)" == "$(readlink /proc/1/ns/net)" ]]; then echo 'Refusing to change the primary network namespace.' >&2; exit 1; fi
dotnet_command="${DOTNET_ROOT:+$DOTNET_ROOT/}dotnet"
configuration="${CONFIGURATION:-Release}"
build_root="${DITUNNEL_BUILD_ROOT:-$repository_root/artifacts/linux/build}"
runtime_root="$repository_root/.tools/linux-runtime/linux-x64"
temp_root="$(mktemp -d /tmp/ditunnel-vpn-peer-XXXXXX)"
peer_pid=''
python_pid=''
cleanup() {
    [[ -z "$python_pid" ]] || kill "$python_pid" 2>/dev/null || true
    [[ -z "$peer_pid" ]] || kill "$peer_pid" 2>/dev/null || true
    [[ -z "$python_pid" ]] || wait "$python_pid" 2>/dev/null || true
    [[ -z "$peer_pid" ]] || wait "$peer_pid" 2>/dev/null || true
    rm -f -- "$temp_root/peer.py" "$temp_root/probe.crt" "$temp_root/probe.key"
    rmdir -- "$temp_root"
}
trap cleanup EXIT
unshare --net -- sleep infinity & peer_pid=$!
for attempt in {1..50}; do [[ "$(readlink "/proc/$peer_pid/ns/net")" != "$(readlink /proc/self/ns/net)" ]] && break; sleep 0.02; done
ip link set lo up
ip link add uplink type veth peer name peer
ip link set peer netns "$peer_pid"
ip address add 10.77.0.1/24 dev uplink
ip -6 address add fd77::1/64 dev uplink nodad
ip link set uplink up
nsenter -t "$peer_pid" -n ip link set lo up
nsenter -t "$peer_pid" -n ip address add 10.77.0.2/24 dev peer
nsenter -t "$peer_pid" -n ip -6 address add fd77::2/64 dev peer nodad
nsenter -t "$peer_pid" -n ip link set peer up
nsenter -t "$peer_pid" -n ip address add 198.51.100.20/32 dev lo
nsenter -t "$peer_pid" -n ip -6 address add 2001:db8:77::20/128 dev lo nodad
ip route add default via 10.77.0.2 dev uplink
ip -6 route add default via fd77::2 dev uplink
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=dns.google \
    -addext 'subjectAltName=DNS:one.one.one.one,DNS:dns.google' \
    -keyout "$temp_root/probe.key" -out "$temp_root/probe.crt" 2>/dev/null
cat > "$temp_root/peer.py" <<'PY'
import http.server, socket, ssl, sys, threading
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body=b'VPN_SYNTHETIC'
        self.send_response(200); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self,*args): pass
class Server6(http.server.HTTPServer): address_family=socket.AF_INET6
for server_type, address in [(http.server.HTTPServer,'198.51.100.20'),(Server6,'2001:db8:77::20')]:
    threading.Thread(target=server_type((address,18081),Handler).serve_forever,daemon=True).start()
class ControlHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(503 if self.headers['Host'] == 'one.one.one.one' else 200)
        self.end_headers()
    def log_message(self,*args): pass
# Only the synthetic VPN server can reach this listener in the peer namespace.
control = http.server.ThreadingHTTPServer(('127.0.0.1',18443),ControlHandler)
tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
tls.load_cert_chain(*sys.argv[1:])
control.socket = tls.wrap_socket(control.socket, server_side=True)
threading.Thread(target=control.serve_forever,daemon=True).start()
def echo(family,address):
    sock=socket.socket(family,socket.SOCK_DGRAM); sock.bind((address,18082))
    while True:
        data,source=sock.recvfrom(4096); sock.sendto(data,source)
for family,address in [(socket.AF_INET,'198.51.100.20'),(socket.AF_INET6,'2001:db8:77::20')]:
    threading.Thread(target=echo,args=(family,address),daemon=True).start()
threading.Event().wait()
PY
nsenter -t "$peer_pid" -n python3 "$temp_root/peer.py" "$temp_root/probe.crt" "$temp_root/probe.key" & python_pid=$!
SSL_CERT_FILE="$temp_root/probe.crt" dbus-run-session -- "$dotnet_command" "$build_root/DiTunnel.Linux.Smoke/bin/$configuration/net10.0/DiTunnel.Linux.Smoke.dll" \
    --vpn-namespace "$runtime_root" "$peer_pid"
