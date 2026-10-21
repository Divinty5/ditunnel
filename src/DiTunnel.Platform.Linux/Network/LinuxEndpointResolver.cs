using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Linux.Network;

// Resolve before stopping the old tunnel. A short cache keeps the same endpoint
// available during a protected handoff or a retry after a core failure.
public sealed class LinuxEndpointResolver(TimeProvider? timeProvider = null, Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, IPAddress[] Addresses)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<IPAddress> ResolveAsync(ImportedProfile profile, bool ipv4Only, CancellationToken token, bool protectedRetry = false)
    {
        token.ThrowIfCancellationRequested();
        var host = AmneziaWgProfileConverter.IsAmneziaWg(profile)
            ? AmneziaWgProfileConverter.Convert(profile).ServerHost : XrayProfileConverter.Convert(profile).ServerHost;
        if (!cache.TryGetValue(host, out var entry) || entry.Until < time.GetUtcNow() && !protectedRetry)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var addresses = await (lookup is null ? Dns.GetHostAddressesAsync(host, timeout.Token) : lookup(host, timeout.Token)).ConfigureAwait(false);
            if (cache.Count >= 1024)
            {
                var oldest = cache.OrderBy(pair => pair.Value.Until).FirstOrDefault();
                cache.TryRemove(oldest.Key, out _);
            }
            cache[host] = entry = (time.GetUtcNow().AddMinutes(2), addresses);
        }
        return entry.Addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork)
            ?? (ipv4Only ? null : entry.Addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetworkV6))
            ?? throw new InvalidOperationException("Не удалось определить подходящий IP-адрес VPN-сервера.");
    }
}
