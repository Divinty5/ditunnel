# Windows TUN prototype: manual validation

The UI remains `asInvoker`. For this prototype launch the client elevated with `scripts/Start-DiTunnel.ps1`, or run its EXE as administrator. Xray/Wintun, route setup and DNS policy need elevated privileges. A Windows service with an unelevated UI is a future step.

## Implemented

- Profile/subscription groups, group selection, server selection and whole-group deletion. Existing flat records appear under «Ранее импортированные». Deletion is saved before the in-memory list is changed. A failed save leaves the group intact.
- Adaptive server list and horizontally centered power button. Small windows retain vertical scrolling.
- Generated Xray outbounds for HY2, VLESS (TCP, WS, HTTPUpgrade), Trojan and SIP002 Shadowsocks. Unsupported transports/options fail explicitly. Arbitrary imported JSON is not passed to an elevated runtime.
- Loopback SOCKS preflight before any OS routing changes, with TLS certificate-error classification.
- Separate hidden PowerShell network host, guarded by a global mutex. It starts Xray, configures the DiTunnel adapter, adds a server IPv4 /32 bypass, temporary split default IPv4/IPv6 routes, and an owned NRPT DNS rule. Physical adapter DNS and default routes are not modified. Existing DNS policy and another virtual default route cause a refusal.
- Connected state follows a successful HTTPS request to `https://1.1.1.1/cdn-cgi/trace` after checking that Windows selects the TUN route. This endpoint is also used by preflight; it sees the exit IP. No IP or response is stored.
- Disconnect/window close signal the host to roll back. If the UI or Xray exits, the host performs cleanup independently. Network adapter loss stops the tunnel. Raw Xray diagnostics are not shown or saved.

## Validation performed on 2026-09-05

- Solution build: no warnings/errors; 33 unit tests passed.
- Actual Avalonia renders checked at wide and 400-pixel widths.
- Four imported server configurations passed pinned Xray 26.3.27 validation.
- HTTPS via loopback Xray passed for two VLESS servers and Shadowsocks.
- HY2 failed certificate verification: certificate names do not match the profile SNI. Fix the server certificate/SNI; verification is not disabled by the client.
- Full elevated TUN/routing/DNS validation has **not** been performed. The user chose to perform it manually; another TUN adapter was active during development.

## First manual run

1. Disconnect Hiddify/other VPN and close any older Di-Tunnel window.
2. Run `scripts/Start-DiTunnel.ps1` and accept the Windows UAC prompt.
3. Select a working VLESS or Shadowsocks server and press «Подключить».
4. Confirm «VPN подключён», internet access, and the expected exit address. Check IPv4, IPv6 and DNS separately; one HTTPS probe does not prove all three.
5. Disconnect and check ordinary internet access. Repeat once, then connect and close the application to check cleanup.

If incomplete cleanup is reported, wait for the host to exit, then run `scripts/Repair-DiTunnelNetwork.ps1` elevated. It removes only the owned stale NRPT rule and refuses to run while the host/TUN is active. It does not reset system-wide DNS, firewall or other VPN settings.

## Prototype limits

No kill switch, installed service, automatic reconnection, verified sleep/wake handling, per-app rules, ICMP or automatic subscription refresh. Physical network changes need a manual reconnect. LAN-specific routes remain preferred. IPv6 routes are installed but dual-stack and DNS leak behavior still need live validation. Forced termination of the independent network host itself is outside the tested cleanup path. Power loss can leave the persistent NRPT rule; the next elevated connection or repair script removes this owned rule. Do not treat the prototype as leak-proof.

## Sources

- [Pinned Xray TUN behavior](https://github.com/XTLS/Xray-core/blob/v26.3.27/proxy/tun/README.md)
- [Pinned TUN configuration](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/tun.go)
- [Pinned Hysteria transport configuration](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/transport_internet.go)

## Update 0.2.1 — 2026-09-06

- Reproduced the connection stall: Windows PowerShell 5 synchronously blocks inside `Console.In.ReadLineAsync()`. The production host now uses a stop-marker file and parent-process monitoring, so startup no longer waits for a disconnect command. Real PowerShell regression tests execute the production script with mocked network commands and verify both failure rollback and cancellation.
- Added connection cancellation, named startup stages and an eight-second upper bound for closing the UI. Failed cleanup no longer permanently vetoes window close. The independent host continues rollback after the owner exits.
- HY2 HTTPS through the real server passed with certificate verification enabled. For an IP endpoint, an empty SNI or the placeholder `hysteria` now uses the endpoint IP for certificate verification. Explicit DNS SNI values are preserved. Xray still receives `protocol: hysteria` and `version: 2`.
- Added selected-server and whole-profile HTTPS timing through local Xray SOCKS. Each check has a timeout and can be cancelled; it does not change Windows routing or require elevation. This is end-to-end HTTPS timing through the proxy, not direct RTT/ICMP. An existing VPN may affect the measured path.
- Four full server rows are visible in the default 900-pixel-high window. Narrow/short windows retain page scrolling and the list itself reserves space for four rows.
- `last-network.log` in `%LOCALAPPDATA%/DiTunnel` contains timestamps, allowlisted stage/error codes and the host exit code. No subscription URLs, addresses, credentials or raw Xray logs are written there.
- Full elevated TUN connectivity and cleanup still require the user's manual check. Use the window title **Di-Tunnel 0.2.1** to distinguish this build from earlier intermediate builds.

## Update 0.2.2 — 2026-09-06

The reported DNS-stage failure could not be identified from the retained log: a later attempt had overwritten it with `ERROR_OTHER_VPN`. The original `DNS` stage included NRPT rule creation, cache flushing and route selection, so its name did not identify the failing operation.

The host now waits for the assigned IPv4 address to become Preferred, reports DNS_RULE/DNS_CACHE/ROUTE_CHECK separately, and allows up to five seconds for Windows to select the TUN route. Error records include only allowlisted command names, exception/category codes and native error codes. These changes address a possible readiness race; a full elevated repro is still needed to confirm the original failure's cause.

The **Журналы** button opens the current Windows user's `%LOCALAPPDATA%/DiTunnel` directory. `last-network.log` is the latest attempt; previous nonempty logs are copied into `logs` before a new attempt starts. This directory is separate from the Git repository and application executable directory.

Server timing has not been changed to match another client. It times a fresh HTTPS request through the selected Xray outbound, including proxy/TLS handshakes but excluding Xray process startup. Different probe endpoints, connection reuse, sample aggregation or an already active VPN can yield different numbers. No specific Hiddify measurement method has been verified.


## Windows 11 verification, 2026-09-06

The user reported successful TUN connections using all four subscription entries: VLESS WebSocket, VLESS HTTPUpgrade, Shadowsocks, and Hysteria 2. The last ten network attempts contain CONNECTED followed by STOPPED and EXIT_0 without cleanup errors. CONNECTED is emitted only after the tunnel HTTPS check succeeds. A read-only check after disconnect found no DiTunnel adapter, routes or managed NRPT rule.

This confirms connection and normal cleanup for these attempts. DNS/IPv6 packet leakage, sleep/resume, interface changes and long-running stability still require dedicated tests; this is not a kill-switch validation.

Version 0.3.0 adds desktop settings, tray lifecycle, subscription refresh/limits and packaging. Connection delay in the tray is the last HTTPS measurement, including connection setup, not continuous ICMP ping.

## Update 0.3.4 — 2026-09-07

- Added split-tunnelling modes for all traffic, selected-domain bypass, and selected domains through VPN. Input is normalised to host names and saved automatically when leaving Settings.
- The elevated host resolves selected domains before connection and refreshes its owned IPv4/IPv6 host routes about every 45 seconds. This was manually tested with api4.ipify.org and ifconfig.me: in bypass mode both returned the direct address; in selected-only mode both returned the VPN address.
- A profile can now be selected while the VPN is active. Di-Tunnel disconnects and reconnects using the newly selected profile.
- The subscription/profile list now uses responsive tiles, orders successful checks by latency, places timed-out profiles last, and can immediately choose the lowest-latency profile when Lowest is enabled.
- The world map uses fixed SVG coordinates for lights, so they scale with their associated continents. Lights fade independently and retain an inner margin from coastline polygons.
- Di-Tunnel-0.3.4-Setup-x64.exe is built as a self-contained x64 installer with the pinned Xray runtime, Wintun and the network recovery script.

The following remain outside the verified scope: kill-switch behavior, sleep/wake and interface-change recovery, exhaustive IPv6/DNS leak tests, per-process split rules, and multi-domain service definitions such as YouTube.

## Update 0.3.5 — 2026-09-07

- On a corporate Windows workstation, VLESS HTTPUpgrade, VLESS WebSocket, Shadowsocks and Hysteria 2 each established a TUN connection during manual testing. With Hysteria 2 active, `api4.ipify.org`, `ifconfig.me/ip`, Telegram Web and YouTube used the VPN egress address.
- Other active VPN clients are detected before Di-Tunnel changes TUN addresses or routes. The checked HTTPS probe reaches the configured server before a tunnel is created; it does not by itself prove TUN routing.
- Error notices can be copied from the main screen. Selecting another subscription profile while connected starts an automatic disconnect and reconnect sequence.
- The Windows tray icon, dynamic connection status and menu have been restored after a temporary diagnostic isolation test.
- Known workstation-specific issue: on this corporate device, the desktop process can terminate with Windows event `0xC0000005` in `ntdll.dll` after several consecutive automatic profile switches. The issue persisted after rebooting a pending Kaspersky Endpoint Security update and with the tray disabled. Individual connection attempts remain usable; the root cause could not be established without a permitted native crash dump.
- Di-Tunnel-0.3.5-Setup-x64.exe is built as a self-contained x64 installer with the pinned Xray runtime, Wintun and the network recovery script.
