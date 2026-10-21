using System.Runtime.Versioning;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

[SupportedOSPlatform("linux")]
internal static class LinuxPrivateSubscriptionSmoke
{
    public static async Task RunAsync(string path)
    {
        if (File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim() != "26de44ca-6663-4ad0-99d1-8ac3913b6d0f"
            || File.Exists("/run/ditunnel/vpn-owner.json")) throw new InvalidOperationException("Expected idle owned Ubuntu VM.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var token = deadline.Token;
        var profiles = ProfileParser.Parse(await File.ReadAllTextAsync(path, token)).ToList();
        using var client = new LinuxNetworkClient(policy: () => new(SplitTunnelMode.ProxyAll, [], [], true, true));
        var available = new List<(ImportedProfile Profile, ServerProbeMode Mode)>();
        foreach (var profile in profiles)
            foreach (var mode in new[] { ServerProbeMode.Fast, ServerProbeMode.Https })
            {
                var result = await client.ProbeAsync(profile, token, mode);
                if (result.Milliseconds is not null) available.Add((profile, mode));
                Console.WriteLine($"BASELINE_REAL_PROFILE_{profiles.IndexOf(profile)}_{mode}_AVAILABLE_{result.Milliseconds is not null}");
            }
        if (available.Count == 0) throw new Exception("No reachable private profiles");
        var countries = new LinuxServerCountryResolver("/usr/lib/ditunnel/runtime/geoip.dat");
        var count = 0;
        foreach (var profile in profiles)
            if (await countries.ResolveAsync(profile, token) is { Length: 2 }) count++;
        if (count == 0) throw new Exception("No country flags resolved from bundled database");
        Console.WriteLine("PASS_REAL_PROFILE_COUNTRIES_FROM_BUNDLED_GEOIP_COUNT_" + count);
        try
        {
            await client.ConnectAsync(available[0].Profile, token);
            await HttpsAsync(token);
            foreach (var item in available)
            {
                var result = await client.ProbeAsync(item.Profile, token, item.Mode);
                Console.WriteLine($"KS_REAL_PROFILE_{profiles.IndexOf(item.Profile)}_{item.Mode}_RESULT_{result.Message}");
                if (result.Milliseconds is null)
                {
                    await Task.Delay(1000, token);
                    result = await client.ProbeAsync(item.Profile, token, item.Mode);
                    Console.WriteLine($"KS_REAL_PROFILE_{profiles.IndexOf(item.Profile)}_{item.Mode}_RETRY_{result.Message}");
                }
                if (result.Milliseconds is null || !result.Message.StartsWith(item.Mode == ServerProbeMode.Https ? "HTTPS ·" : "HTTP ·", StringComparison.Ordinal))
                    throw new Exception("Previously reachable profile failed under KS");
                Console.WriteLine("PASS_REAL_NONACTIVE_PROFILE_PROBE_WITH_KS_" + item.Mode);
            }
            foreach (var profile in available.Select(item => item.Profile).Distinct())
            {
                await client.SwitchAsync(profile, token);
                if (!client.IsNetworkProtectionActive) throw new Exception("Protected switch lost KS");
                await HttpsAsync(token);
                Console.WriteLine("PASS_REAL_PROTECTED_SWITCH_AND_HTTPS");
            }
            await client.DisconnectAsync(token);
            await client.RestoreNetworkAsync(token);
            await client.ConnectAsync(available[0].Profile, token);
            await HttpsAsync(token);
            await client.DisconnectAsync(token);
            Console.WriteLine("PASS_REAL_DISCONNECT_RESTORE_RECONNECT_SAME_CLIENT");
        }
        finally { await client.RestoreNetworkAsync(CancellationToken.None); }
    }

    private static async Task HttpsAsync(CancellationToken token)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await http.GetAsync("https://1.1.1.1/cdn-cgi/trace", token);
        response.EnsureSuccessStatusCode();
    }
}
