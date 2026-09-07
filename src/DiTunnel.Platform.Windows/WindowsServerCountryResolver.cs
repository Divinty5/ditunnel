using System.Collections.Concurrent;
using System.Net;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Windows;

public sealed class WindowsServerCountryResolver : IServerCountryResolver
{
    private readonly Lazy<Task<GeoIpDatabase>> database = new(() => Task.Run(() =>
        GeoIpDatabase.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Runtime", "geoip.dat")))));
    private readonly ConcurrentDictionary<string, Task<string?>> cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> ResolveAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var host = XrayProfileConverter.Convert(profile).ServerHost;
            return await cache.GetOrAdd(host, LookupAsync).WaitAsync(cancellationToken);
        }
        catch { return null; }
    }
    private async Task<string?> LookupAsync(string host)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            var geo = await database.Value.WaitAsync(timeout.Token);
            return addresses.Select(geo.FindCountry).FirstOrDefault(code => code is not null);
        }
        catch { return null; }
    }
}
