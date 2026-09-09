# Security policy

## System and scope

Di-Tunnel is a client application that controls Xray-core, a TUN interface, DNS and operating-system routes. This policy covers the application, platform integrations, configuration import, update and packaging code, build scripts, and release artifacts in this repository.

Security fixes are provided for the latest published `0.3.x` release. Older pre-release builds should be upgraded before reporting a problem that is already corrected in the current release.

## Threat model and trust boundaries

Imported profiles, subscription responses, QR codes, Xray output, DNS responses, network metadata and update metadata are untrusted input. Xray-core and other downloaded native components are external dependencies and must be authenticated before execution. The UI process, future privileged Windows service, Xray process and operating-system networking APIs are separate trust boundaries.

The primary assets are VPN credentials, subscription tokens, signing keys, local network configuration, diagnostic data and the integrity of executable updates. A remote endpoint or imported profile must not gain arbitrary command execution or access to files outside the application's owned data directories.

## Security invariants

- Real credentials, subscription URLs, QR codes, private keys, signing material and generated live configurations must never be committed, logged or included in build artifacts.
- Examples and tests must use fictitious, non-routable or explicitly disposable values.
- User-controlled values must be passed as structured process arguments and must not be interpolated into shell commands.
- Downloaded executables and updates must be obtained from an approved source and verified against pinned integrity metadata before execution.
- Privileged components must expose only the minimum VPN operations and authenticate their local caller. The unprivileged UI must not be able to request arbitrary commands, executable paths or system changes.
- Network, DNS and route changes must be attributable to Di-Tunnel and recoverable after cancellation, process failure or application restart.
- Logs and diagnostic exports must remove credentials and subscription tokens before display or persistence.

## Repository secret policy

The following may be committed:

- source code, tests, documentation and build scripts;
- pinned public dependency versions, download URLs and cryptographic checksums;
- configuration schemas and examples containing only fictitious values;
- sanitized logs and diagnostic fixtures that have been reviewed for identifiers and credentials.

The following must not be committed:

- live subscription URLs or tokens, QR codes and downloaded subscription responses;
- VLESS UUIDs, Shadowsocks passwords, Hysteria authentication strings and obfuscation passwords;
- private keys, certificate private keys, API tokens, cookies, signing keys or keystores;
- real generated Xray configurations, local profile databases or credential-store exports;
- unsanitized logs, crash dumps, packet captures or diagnostic reports;
- production server inventory, management URLs or deployment credentials.

Public server addresses, SNI values, certificate chains and protocol settings may not be credentials by themselves, but project examples should still replace deployment-specific values with fictitious ones. Hashing a secret does not make it safe to commit.

Local development secrets belong in ignored `*.local.*`, `*.secret.*`, `.env` or `.tools/local` files. Product credentials must later use an operating-system credential store rather than plaintext project files.

## Reportable findings and severity context

Report vulnerabilities that can expose VPN traffic or credentials, bypass the selected routing or DNS policy, execute attacker-controlled code, cross the UI/service privilege boundary, accept an unauthenticated update, leave the system in an unsafe network state, or disclose sensitive diagnostics. Severity depends on realistic reachability, required user interaction, privilege gained, persistence and the amount of traffic or secret material exposed.

Dependency reports are actionable when they affect a reachable Di-Tunnel code path or distributed artifact. A version match without demonstrated applicability is not sufficient by itself.

## Known limitations

- The Windows UI currently runs elevated; privilege separation into a dedicated authenticated service is not complete.
- The installer is not code-signed and application updates are not installed automatically. Users must obtain releases from this repository and may verify the published SHA-256 checksum.
- Split tunnelling by process is not implemented.
- VMess and arbitrary Xray JSON may be stored but are not executed as VPN connections.
- The tested kill-switch and DNS guarantees apply to the documented Windows 11 VM scenarios and cannot compensate for a compromised operating system or VPN server.

Profiles are encrypted at rest with Windows DPAPI for the current user. Network diagnostics use an allowlist and do not persist raw Xray stderr, credentials or subscription URLs. TUN routing, IPv4/IPv6 WFP filtering, DNS routing and owned-state cleanup are implemented and covered by automated and VM tests.

## Reporting a vulnerability

Do not disclose a vulnerability or real credentials in a public issue. Use GitHub's private vulnerability reporting for this repository and include affected versions, reproduction steps, impact and suggested remediation when available. Use newly generated disposable credentials only when reproduction requires them, then revoke them after the report.
