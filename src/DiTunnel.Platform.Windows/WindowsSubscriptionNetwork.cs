using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows;

public static class WindowsSubscriptionNetwork
{
    public static HttpClient CreateClient() => DirectSubscriptionHttp.Create(ConnectAsync);

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var uplink = WindowsNetworkApi.FindUplink();
        var index = checked((int)uplink.InterfaceIndex);
        var ipv6Index = NetworkInterface.GetAllNetworkInterfaces().Select(n => n.GetIPProperties())
            .FirstOrDefault(n => n.GetIPv4Properties()?.Index == index)?.GetIPv6Properties()?.Index;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal]
            : await Task.Run(() => Resolve(endpoint.Host, uplink.InterfaceIndex, ipv6Index is not null), token)
                .WaitAsync(token).ConfigureAwait(false);
        return await DirectSubscriptionHttp.ConnectAsync(
            addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork || ipv6Index is not null),
            endpoint.Port, socket =>
            {
                const SocketOptionName unicastInterface = (SocketOptionName)31;
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                    socket.SetSocketOption(SocketOptionLevel.IP, unicastInterface, IPAddress.HostToNetworkOrder(index));
                else
                    socket.SetSocketOption(SocketOptionLevel.IPv6, unicastInterface, ipv6Index!.Value);
            }, token).ConfigureAwait(false);
    }

    private static IPAddress[] Resolve(string host, uint index, bool ipv6)
    {
        var addresses = new List<IPAddress>();
        Query(1, 4);
        if (ipv6 && addresses.Count == 0) Query(28, 16);
        return addresses.ToArray();

        unsafe void Query(ushort type, int addressLength)
        {
            fixed (char* name = host)
            {
                const ulong bypassCache = 0x8, wireOnly = 0x100;
                var request = new DnsRequest { Version = 1, Name = (nint)name, Type = type, Options = bypassCache | wireOnly, InterfaceIndex = index };
                var result = new DnsResult { Version = 1 };
                try
                {
                    if (DnsQueryEx(ref request, ref result, 0) != 0) return;
                    for (var record = (DnsRecord*)result.Records; record != null; record = record->Next)
                        if (record->Type == type && record->Length == addressLength)
                            addresses.Add(new IPAddress(new ReadOnlySpan<byte>(&record->Address, addressLength)));
                }
                finally { if (result.Records != 0) DnsFree(result.Records, 1); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRequest
    {
        public uint Version;
        public nint Name;
        public ushort Type;
        public ulong Options;
        public nint Servers;
        public uint InterfaceIndex;
        public nint Callback, Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsResult
    {
        public uint Version, Status;
        public ulong Options;
        public nint Records, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DnsRecord
    {
        public DnsRecord* Next;
        public nint Name;
        public ushort Type, Length;
        public uint Flags, TimeToLive, Reserved, Address;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsQueryEx(ref DnsRequest request, ref DnsResult result, nint cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsFree(nint data, int freeType);
}
