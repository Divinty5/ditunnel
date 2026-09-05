# Di-Tunnel

Di-Tunnel is a cross-platform VPN client built around Xray-core. The first target is Windows; Android and Linux will follow after the Windows connection lifecycle is stable.

The project focuses on explicit control over TUN routing, DNS, reconnection, profiles and diagnostics. VPN protocols and cryptography are provided by Xray-core.

![Di-Tunnel icon](icon.png)

## Current status

The project is in the planning and network-prototyping stage. The first milestone is a Windows client that connects to one Hysteria 2 server, routes system traffic through TUN and reliably restores the original network state on disconnect or failure.

- [Product idea](Di-Tunnel_idea.md)
- [Development plan](docs/development-plan.md)

## Planned technology

- C# and .NET 10
- Avalonia UI
- Xray-core
- Visual Studio 2026 as the primary development environment

No real server addresses, credentials, subscription URLs, signing keys or generated runtime configurations may be committed. Example configuration must use fictitious values.

## Development

Requirements:

- Visual Studio 2026 with the .NET desktop development workload, or .NET SDK 10.0.302+

Open `DiTunnel.sln` in Visual Studio and set `DiTunnel.Desktop` as the startup project, or run from a terminal:

```powershell
dotnet restore
dotnet build
dotnet run --project src/DiTunnel.Desktop
```

The current application is a UI scaffold. Its Connect button remains disabled until the Xray lifecycle is implemented.

### Xray-core runtime

Install the pinned Windows x64 runtime into the ignored `.tools` directory:

```powershell
.\scripts\Install-Xray.ps1
```

The script downloads the official release archive and verifies its SHA-256 before extraction. The pinned version and digest are stored in `eng/xray-version.json`; runtime binaries are never committed.

## License

No license has been selected yet. The source is publicly visible, but no reuse rights are granted until a license is added.
