using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Windows.Network;

internal static class WindowsVpnCoexistence
{
    internal static bool IsOtherVpn(string name, string description, NetworkInterfaceType type, bool up, string ownName) =>
        up && !name.Equals(ownName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith("DiTunnel", StringComparison.OrdinalIgnoreCase)
        && (type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
            || new[] { "OpenVPN", "TAP-Windows", "WireGuard", "Wintun", "Fortinet", "Cisco AnyConnect" }
                .Any(marker => (name + " " + description).Contains(marker, StringComparison.OrdinalIgnoreCase)));

    internal static HashSet<uint> Interfaces(string ownName) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(item => item.Supports(NetworkInterfaceComponent.IPv4)
            && IsOtherVpn(item.Name, item.Description, item.NetworkInterfaceType, item.OperationalStatus == OperationalStatus.Up, ownName)
            && item.GetIPProperties().UnicastAddresses.Any(address => !IPAddress.IsLoopback(address.Address) && !address.Address.IsIPv6LinkLocal))
        .Select(item => (uint)(item.GetIPProperties().GetIPv4Properties()?.Index ?? 0)).Where(index => index != 0).ToHashSet();

    internal static bool CoversSpecificRoute(string prefix, IPAddress address)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var bits)) return false;
        var bytes = network.GetAddressBytes(); var target = address.GetAddressBytes();
        // A full-tunnel default must not exempt the entire Internet from Di-Tunnel.
        if (bytes.Length != target.Length || bits <= 1 || bits > bytes.Length * 8) return false;
        for (var index = 0; index < bytes.Length && bits > 0; index++, bits -= 8)
        {
            var mask = 255 << (8 - Math.Min(8, bits));
            if ((bytes[index] & mask) != (target[index] & mask)) return false;
        }
        return true;
    }

    internal static void ConfigureOpenVpnBypass(JsonObject root, string physicalSource, bool enabled, string? physicalInterface = null)
    {
        if (!enabled) return;
        if (root["routing"] is not JsonObject) root["routing"] = new JsonObject();
        if (root["routing"]!["rules"] is not JsonArray) root["routing"]!["rules"] = new JsonArray();
        // Match the transport owner, not a hardcoded corporate address or all UDP/1194.
        root["outbounds"]!.AsArray().Add(new JsonObject
        {
            ["tag"] = "openvpn-direct", ["protocol"] = "freedom", ["sendThrough"] = physicalSource,
            ["settings"] = new JsonObject { ["domainStrategy"] = "UseIPv4" },
            ["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = physicalInterface ?? "" } }
        });
        root["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject
        {
            ["process"] = new JsonArray("openvpn", "openvpnserv", "openvpn-gui", "OpenVPNConnect", "ovpnconnector"),
            ["outboundTag"] = "openvpn-direct"
        });
    }
}
