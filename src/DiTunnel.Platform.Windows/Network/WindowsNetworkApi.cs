using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.Networking.WinSock;

namespace DiTunnel.Platform.Windows.Network;

internal sealed record NetworkRoute(string Prefix, uint InterfaceIndex, string NextHop, uint Metric = 1);
internal sealed record PhysicalUplink(uint InterfaceIndex, string NextHop, string SourceAddress);

/// <summary>IP Helper operations never execute shell commands and only remove routes they created.</summary>
internal static class WindowsNetworkApi
{
    internal static void Check(WIN32_ERROR error)
    {
        if (error != WIN32_ERROR.NO_ERROR) throw new Win32Exception((int)error);
    }

    internal static unsafe SOCKADDR_INET SocketAddress(IPAddress address)
    {
        SOCKADDR_INET result = default;
        result.si_family = (ADDRESS_FAMILY)(address.AddressFamily == AddressFamily.InterNetwork ? 2 : 23);
        var bytes = new Span<byte>(&result, sizeof(SOCKADDR_INET));
        address.GetAddressBytes().CopyTo(bytes[(address.AddressFamily == AddressFamily.InterNetwork ? 4 : 8)..]);
        return result;
    }

    internal static unsafe IPAddress Address(SOCKADDR_INET address)
    {
        var bytes = new ReadOnlySpan<byte>(&address, sizeof(SOCKADDR_INET));
        return address.si_family == (ADDRESS_FAMILY)2 ? new(bytes.Slice(4, 4)) : new(bytes.Slice(8, 16));
    }

    internal static unsafe IReadOnlyList<NetworkRoute> Routes()
    {
        Check(PInvoke.GetIpForwardTable2((ADDRESS_FAMILY)0, out MIB_IPFORWARD_TABLE2* table));
        try
        {
            var result = new List<NetworkRoute>();
            foreach (var row in table->Table.AsSpan((int)table->NumEntries))
                result.Add(new($"{Address(row.DestinationPrefix.Prefix)}/{row.DestinationPrefix.PrefixLength}",
                    row.InterfaceIndex, Address(row.NextHop).ToString(), row.Metric));
            return result;
        }
        finally { PInvoke.FreeMibTable(table); }
    }

    internal static bool IsPhysical(uint index)
    {
        var row = new MIB_IF_ROW2 { InterfaceIndex = index };
        return PInvoke.GetIfEntry2(ref row) == WIN32_ERROR.NO_ERROR &&
            row.InterfaceAndOperStatusFlags.HardwareInterface && row.OperStatus == IF_OPER_STATUS.IfOperStatusUp;
    }

    internal static uint Metric(uint index, ADDRESS_FAMILY family)
    {
        var row = new MIB_IPINTERFACE_ROW { InterfaceIndex = index, Family = family };
        Check(PInvoke.GetIpInterfaceEntry(ref row));
        return row.Metric;
    }

    internal static PhysicalUplink FindUplink()
    {
        var route = Routes().Where(r => r.Prefix == "0.0.0.0/0" && r.NextHop != "0.0.0.0" && IsPhysical(r.InterfaceIndex))
            .OrderBy(r => (ulong)r.Metric + Metric(r.InterfaceIndex, (ADDRESS_FAMILY)2)).FirstOrDefault()
            ?? throw new InvalidOperationException("Не найден активный физический интернет-интерфейс.");
        var source = NetworkInterface.GetAllNetworkInterfaces()
            .First(n => n.GetIPProperties().GetIPv4Properties()?.Index == route.InterfaceIndex)
            .GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
                a.DuplicateAddressDetectionState is DuplicateAddressDetectionState.Preferred or DuplicateAddressDetectionState.Deprecated)
            ?? throw new InvalidOperationException("Не найден физический IPv4-адрес.");
        return new(route.InterfaceIndex, route.NextHop, source.Address.ToString());
    }

    internal static uint BestInterface(IPAddress destination)
    {
        Check(PInvoke.GetBestRoute2(null, 0, null, SocketAddress(destination), 0, out var route, out _));
        return route.InterfaceIndex;
    }

    private static MIB_IPFORWARD_ROW2 Row(NetworkRoute route)
    {
        PInvoke.InitializeIpForwardEntry(out var row);
        var prefix = route.Prefix.Split('/');
        row.InterfaceIndex = route.InterfaceIndex;
        row.DestinationPrefix.Prefix = SocketAddress(IPAddress.Parse(prefix[0]));
        row.DestinationPrefix.PrefixLength = byte.Parse(prefix[1], System.Globalization.CultureInfo.InvariantCulture);
        row.SitePrefixLength = 0;
        row.NextHop = SocketAddress(IPAddress.Parse(route.NextHop));
        row.Metric = route.Metric;
        row.Protocol = NL_ROUTE_PROTOCOL.RouteProtocolNetMgmt;
        row.Loopback = false;
        row.AutoconfigureAddress = false;
        row.Publish = false;
        row.Immortal = false;
        return row;
    }

    internal static bool AddRoute(NetworkRoute route)
    {
        var row = Row(route);
        // A pre-existing route is borrowed, never owned or deleted during rollback.
        if (PInvoke.GetIpForwardEntry2(ref row) == WIN32_ERROR.NO_ERROR) return false;
        var error = PInvoke.CreateIpForwardEntry2(Row(route));
        if ((uint)error == 5010) return false; // ERROR_OBJECT_ALREADY_EXISTS: another owner won the race.
        Check(error);
        return true;
    }

    internal static void RemoveRoute(NetworkRoute route)
    {
        var row = Row(route);
        var error = PInvoke.GetIpForwardEntry2(ref row);
        if ((uint)error is 1168 or 2) return; // Interface/route has already disappeared.
        Check(error);
        // Do not remove a route replaced by another application with different parameters.
        if (row.Metric != route.Metric || row.Protocol != NL_ROUTE_PROTOCOL.RouteProtocolNetMgmt) return;
        error = PInvoke.DeleteIpForwardEntry2(row);
        if ((uint)error is not (1168 or 2)) Check(error);
    }

    internal static async Task AddAddressAsync(uint index, string address, byte prefix, CancellationToken token)
    {
        PInvoke.InitializeUnicastIpAddressEntry(out var row);
        row.InterfaceIndex = index;
        row.Address = SocketAddress(IPAddress.Parse(address));
        row.OnLinkPrefixLength = prefix;
        row.PrefixOrigin = NL_PREFIX_ORIGIN.IpPrefixOriginManual;
        row.SuffixOrigin = NL_SUFFIX_ORIGIN.IpSuffixOriginManual;
        var error = PInvoke.CreateUnicastIpAddressEntry(row);
        if ((uint)error != 5010) Check(error);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            token.ThrowIfCancellationRequested();
            Check(PInvoke.GetUnicastIpAddressEntry(ref row));
            if (row.DadState == NL_DAD_STATE.IpDadStatePreferred) return;
            if (row.DadState == NL_DAD_STATE.IpDadStateDuplicate) throw new InvalidOperationException("Адрес TUN-адаптера занят.");
            await Task.Delay(200, token);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Адрес TUN-адаптера не стал доступен.");
    }

    internal static void SetTunnelMetric(uint index, bool ipv6)
    {
        var row = new MIB_IPINTERFACE_ROW { InterfaceIndex = index, Family = (ADDRESS_FAMILY)(ipv6 ? 23 : 2) };
        Check(PInvoke.GetIpInterfaceEntry(ref row));
        row.UseAutomaticMetric = false;
        row.Metric = 1;
        if (!ipv6) row.SitePrefixLength = 0; // Required by SetIpInterfaceEntry for IPv4.
        Check(PInvoke.SetIpInterfaceEntry(ref row));
    }
}
