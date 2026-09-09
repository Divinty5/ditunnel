using System.Net;
using System.Net.Sockets;

namespace DiTunnel.Core.Connection;

/// <summary>
/// Validated input for the Windows filtering host. It deliberately contains only resolved
/// server IPs: resolving a hostname after the switch has engaged could itself leak DNS.
/// </summary>
public enum KillSwitchTransportProtocol { Any, Tcp, Udp, TcpAndUdp }

public sealed record KillSwitchConfiguration(
    IReadOnlyList<IPAddress> ServerAddresses,
    bool AllowLocalNetwork,
    ushort? ServerPort = null,
    KillSwitchTransportProtocol ServerTransport = KillSwitchTransportProtocol.Any,
    IReadOnlyList<IPAddress>? DirectAddresses = null)
{
    public static KillSwitchConfiguration Create(IEnumerable<IPAddress> addresses, bool allowLocalNetwork)
    {
        var unique = addresses
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6None) && !address.Equals(IPAddress.None))
            .Distinct()
            .ToArray();
        if (unique.Length == 0) throw new ArgumentException("Kill switch requires a resolved VPN server address.", nameof(addresses));
        return new(unique, allowLocalNetwork);
    }

    public static KillSwitchConfiguration Create(IEnumerable<IPAddress> addresses, ushort serverPort, KillSwitchTransportProtocol transport, bool allowLocalNetwork, IEnumerable<IPAddress>? directAddresses = null)
    {
        var basic = Create(addresses, allowLocalNetwork);
        if (serverPort == 0) throw new ArgumentOutOfRangeException(nameof(serverPort));
        if (!Enum.IsDefined(transport) || transport == KillSwitchTransportProtocol.Any)
            throw new ArgumentOutOfRangeException(nameof(transport));
        var direct = (directAddresses ?? [])
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6None) && !address.Equals(IPAddress.None))
            .Distinct()
            .ToArray();
        return basic with { ServerPort = serverPort, ServerTransport = transport, DirectAddresses = direct };
    }
}

public enum NetworkProtectionState { Inactive, Activating, Active, Deactivating, Faulted }

public sealed record NetworkProtectionStatus(NetworkProtectionState State, string? Message = null)
{
    public static NetworkProtectionStatus Inactive { get; } = new(NetworkProtectionState.Inactive);
}

/// <summary>
/// A platform-specific controller must install all exceptions before its terminal block rule,
/// keep ownership records, and return Active only after the OS has accepted every filter.
/// </summary>
public interface INetworkProtectionController : IAsyncDisposable
{
    NetworkProtectionStatus Status { get; }
    event EventHandler<NetworkProtectionStatus>? StatusChanged;
    Task ActivateAsync(KillSwitchConfiguration configuration, CancellationToken cancellationToken = default);
    Task DeactivateAsync(CancellationToken cancellationToken = default);
}
