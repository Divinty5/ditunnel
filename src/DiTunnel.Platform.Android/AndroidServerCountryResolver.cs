using Android.Content;
using Android.Util;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Collections.Concurrent;
using System.Net;

namespace DiTunnel.Platform.Android;

public sealed class AndroidServerCountryResolver : IServerCountryResolver
{
    private readonly Context context;
    private readonly Lazy<Task<GeoIpDatabase>> database;
    private readonly ConcurrentDictionary<string, Task<string?>> cache = new(StringComparer.OrdinalIgnoreCase);

    public AndroidServerCountryResolver(Context context)
    {
        this.context = context.ApplicationContext ?? context;
        database = new(() => Task.Run(LoadDatabase));
    }

    public async Task<string?> ResolveAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var host = XrayProfileConverter.Convert(profile).ServerHost;
            return await cache.GetOrAdd(host, LookupAsync).WaitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            Log.Warn("DiTunnelCountry", error.ToString());
            return null;
        }
    }

    private GeoIpDatabase LoadDatabase()
    {
        using var source = context.Assets?.Open("geoip.dat")
            ?? throw new InvalidOperationException("GeoIP-база Android недоступна.");
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return GeoIpDatabase.Load(memory.ToArray());
    }

    private async Task<string?> LookupAsync(string host)
    {
        try
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var parsedAddress))
            {
                addresses = [parsedAddress];
            }
            else
            {
                using var dnsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                addresses = await Dns.GetHostAddressesAsync(host, dnsTimeout.Token);
            }

            // Parsing Xray's 20 MB database can take noticeably longer on a cold Android device.
            // It runs once on a worker thread and all lookups share the completed instance.
            var geo = await database.Value;
            return addresses.Select(geo.FindCountry).FirstOrDefault(code => code is not null);
        }
        catch (Exception error)
        {
            Log.Warn("DiTunnelCountry", error.ToString());
            return null;
        }
    }
}
