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

The repository currently contains the project brief and development plan. Build instructions will be added with the initial solution and application projects.

## License

No license has been selected yet. The source is publicly visible, but no reuse rights are granted until a license is added.
