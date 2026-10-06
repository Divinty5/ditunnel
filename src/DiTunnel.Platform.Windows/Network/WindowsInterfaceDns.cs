using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace DiTunnel.Platform.Windows.Network;

/// <summary>Sets DNS only on the freshly created Di-Tunnel adapter. No physical adapter is changed.</summary>
internal sealed class WindowsInterfaceDns : IDisposable
{
    internal sealed record Adapter(Guid Id, uint Index, string Name, IPAddress[] Servers);
    private readonly Guid interfaceId;
    private readonly Action<Guid, bool, string?> set;
    private readonly List<bool> configuredFamilies = [];
    private WindowsInterfaceDns(Guid interfaceId, Action<Guid, bool, string?> set) { this.interfaceId = interfaceId; this.set = set; }

    internal static WindowsInterfaceDns Configure(uint index, string expectedName, string[] servers) =>
        Configure(index, expectedName, servers, value => Read(value, expectedName), Set);

    internal static WindowsInterfaceDns Configure(uint index, string expectedName, string[] servers,
        Func<uint, Adapter?> read, Action<Guid, bool, string?> set)
    {
        var adapter = FindOwnedAdapter(index, expectedName, read);
        // Windows reports three automatic IPv6 discovery addresses even on a fresh
        // adapter with no configured DNS. They are not a previous owner's settings.
        // Any other pre-existing server still refuses the mutation.
        if (ConfiguredServers(adapter.Servers).Length != 0)
            throw new DnsPolicyException("ADAPTER_NOT_EMPTY");
        var lease = new WindowsInterfaceDns(adapter.Id, set);
        try
        {
            var addresses = servers.Select(IPAddress.Parse).Distinct().ToArray();
            if (addresses.Length == 0) throw new DnsPolicyException("ADAPTER_SERVERS_EMPTY");
            foreach (bool ipv6 in new[] { false, true })
            {
                var family = addresses.Where(address => (address.AddressFamily == AddressFamily.InterNetworkV6) == ipv6).ToArray();
                if (family.Length == 0) continue;
                // Record before the native call so a partial startup always attempts rollback.
                lease.configuredFamilies.Add(ipv6);
                set(lease.interfaceId, ipv6, string.Join(',', family.Select(address => address.ToString())));
            }
            var actual = FindOwnedAdapter(index, expectedName, read);
            if (actual.Id != lease.interfaceId || !ConfiguredServers(actual.Servers).ToHashSet().SetEquals(addresses))
                throw new DnsPolicyException("ADAPTER_VERIFY");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static readonly HashSet<IPAddress> AutomaticDnsDefaults =
        [IPAddress.Parse("fec0:0:0:ffff::1"), IPAddress.Parse("fec0:0:0:ffff::2"), IPAddress.Parse("fec0:0:0:ffff::3")];

    internal static IPAddress[] ConfiguredServers(IEnumerable<IPAddress> servers) => servers.Where(address =>
        address.AddressFamily != AddressFamily.InterNetworkV6 ||
        !AutomaticDnsDefaults.Contains(new IPAddress(address.GetAddressBytes()))).ToArray();

    private static Adapter FindOwnedAdapter(uint index, string expectedName, Func<uint, Adapter?> read)
    {
        if (index == 0 || !expectedName.StartsWith("DiTunnel-", StringComparison.Ordinal))
            throw new DnsPolicyException("ADAPTER_OWNER");
        var adapter = read(index) ?? throw new DnsPolicyException("ADAPTER_MISSING");
        if (adapter.Index != index || adapter.Name != expectedName || adapter.Id == Guid.Empty)
            throw new DnsPolicyException("ADAPTER_OWNER");
        return adapter;
    }

    internal static Adapter? Read(uint index, string expectedName) =>
        Read(index, expectedName, NetworkInterface.GetAllNetworkInterfaces());

    internal static Adapter? Read(uint index, string expectedName, IEnumerable<NetworkInterface> interfaces)
    {
        // Filter by the session's exact name first. GetIPv4Properties throws 10043
        // for non-IP filter interfaces and IPv6-only adapters, even when another
        // interface earlier in the enumeration already matched the requested index.
        var adapter = interfaces.SingleOrDefault(candidate =>
            candidate.Name == expectedName && candidate.Supports(NetworkInterfaceComponent.IPv4) &&
            candidate.GetIPProperties().GetIPv4Properties()?.Index == index);
        return adapter is null ? null : new(Guid.Parse(adapter.Id), index, adapter.Name, adapter.GetIPProperties().DnsAddresses.ToArray());
    }

    private static unsafe void Set(Guid interfaceId, bool ipv6, string? servers)
    {
        string value = servers ?? "";
        fixed (char* names = value)
        {
            var settings = new DNS_INTERFACE_SETTINGS { Version = 1, Flags = ipv6 ? 3ul : 2ul, NameServer = names };
            WindowsNetworkApi.Check(PInvoke.SetInterfaceDnsSettings(interfaceId, &settings));
        }
    }

    public void Dispose()
    {
        List<Exception> errors = [];
        foreach (var ipv6 in configuredFamilies.ToArray())
        {
            try { set(interfaceId, ipv6, null); configuredFamilies.Remove(ipv6); }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 1168) { configuredFamilies.Remove(ipv6); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw errors[0];
    }
}
