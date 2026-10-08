using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Linux.Network;

public interface ILinuxVpnSession : IAsyncDisposable
{
    Task StartAsync(ImportedProfile profile, CancellationToken token);
    Task WaitForExitAsync(CancellationToken token);
    bool IsProtectionActive => false;
    Task StartAsync(ImportedProfile profile, LinuxVpnPolicy policy, CancellationToken token) => StartAsync(profile, token);
    Task ReleaseProtectionAsync() => Task.CompletedTask;
}

[SupportedOSPlatform("linux")]
public sealed class LinuxVpnSession(string runtimeDirectory, string stateDirectory, ILinuxNetworkCommands commands) : ILinuxVpnSession
{
    private readonly string owner = Guid.NewGuid().ToString("N");
    private LinuxRouteLease? routes;
    private XrayProcessManager? xray;
    private LinuxRuntimeServerProbe.AwgLease? bridge;
    private string? directory;
    private bool journalWritten;
    private LinuxKillSwitch? protection;
    public bool IsProtectionActive => protection?.Active == true;
    private string Journal => Path.Combine(stateDirectory, "vpn-owner.json");

    public Task StartAsync(ImportedProfile profile, CancellationToken token) => StartAsync(profile, LinuxVpnPolicy.Default, token);

    public async Task StartAsync(ImportedProfile profile, LinuxVpnPolicy policy, CancellationToken token)
    {
        policy.Validate();
        if (policy.Mode != SplitTunnelMode.ProxyAll && policy.Processes.Length > 0 && !LinuxXrayProcessSupport.IsAvailable(runtimeDirectory))
            throw new NotSupportedException("Для правил приложений нужен обновлённый Linux runtime Di-Tunnel.");
        if (File.Exists(Journal)) throw new InvalidOperationException("Необходимо восстановить предыдущую сессию.");
        var runtime = new LinuxRuntimeServerProbe(runtimeDirectory, stateDirectory);
        var executable = runtime.VerifyRuntime("xray");
        var awg = AmneziaWgProfileConverter.IsAmneziaWg(profile) ? AmneziaWgProfileConverter.Convert(profile) : null;
        var converted = awg is null ? XrayProfileConverter.Convert(profile) : null;
        var addresses = await Dns.GetHostAddressesAsync(awg?.ServerHost ?? converted!.ServerHost, token).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetworkV6) ?? throw new InvalidOperationException();
        var name = "dtn" + owner[..10];
        routes = new(commands, name, owner);
        await routes.PreflightAsync(token).ConfigureAwait(false);
        if (File.Exists(Journal)) throw new InvalidOperationException("Необходимо восстановить предыдущую сессию.");
        // Write intent before the first mutation. This journal contains no endpoint or credentials.
        token.ThrowIfCancellationRequested();
        var temporaryJournal = Path.Combine(stateDirectory, "vpn-owner-" + owner + ".tmp");
        try
        {
            await LinuxRuntimeServerProbe.WritePrivateAsync(temporaryJournal, new JsonObject { ["version"] = 2, ["owner"] = owner, ["killSwitch"] = policy.KillSwitch }.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
            File.Move(temporaryJournal, Journal, overwrite: false);
            journalWritten = true;
        }
        finally { File.Delete(temporaryJournal); }
        directory = Path.Combine(stateDirectory, "vpn-" + owner);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (policy.KillSwitch)
        {
            protection = new(commands as ILinuxFirewallCommands ?? throw new InvalidOperationException("nftables недоступен."), owner);
            await protection.ActivateAsync(name, address, awg?.ServerPort ?? converted!.ServerPort,
                awg is not null ? KillSwitchTransportProtocol.Udp : converted!.ServerTransport, policy.AllowLan, token);
        }
        bridge = awg is null ? null : await runtime.StartAwgAsync(awg, address, directory, token, LinuxRouteLease.Mark).ConfigureAwait(false);
        var configuration = bridge?.Proxy ?? converted!;
        var dns = awg?.DnsServers ?? new[] { "1.1.1.1", "1.0.0.1" };
        var config = BuildConfiguration(configuration, address, name, dns, policy);
        var path = Path.Combine(directory, "xray.json");
        await LinuxRuntimeServerProbe.WritePrivateAsync(path, config, token).ConfigureAwait(false);
        xray = new(new XrayOptions { ExecutablePath = executable, WorkingDirectory = runtimeDirectory,
            ValidateConfigurationBeforeStart = false, ProcessLifetimeFactory = static process => new LinuxProcessLifetime(process) });
        // Xray's '-test' opens TUN; never run it as a read-only validation.
        await xray.StartAsync(path, token).ConfigureAwait(false);
        await routes.ClaimLinkAsync(token).ConfigureAwait(false);
        await routes.ActivateAsync(dns, token).ConfigureAwait(false);
        if (!xray.IsRunning || bridge is { IsRunning: false }) throw new InvalidOperationException("Ядро VPN завершилось.");
    }

    public static string BuildConfiguration(XrayProfileConfiguration configuration, IPAddress endpoint, string name, IReadOnlyList<string> dns, LinuxVpnPolicy? policy = null)
    {
        policy ??= LinuxVpnPolicy.Default;
        policy.Validate();
        var config = JsonNode.Parse(configuration.Build(endpoint.ToString(), tun: true, tunnelName: name, dnsServers: dns,
            splitTunnel: policy.Split, blockAds: policy.BlockAds, strictAdBlocking: policy.StrictAds))!;
        config["inbounds"]![0]!["settings"]!.AsObject().Remove("autoOutboundsInterface");
        config["log"]!["loglevel"] = "none";
        if (policy.KillSwitch && policy.AllowLan)
        {
            var outbounds = config["outbounds"]!.AsArray();
            if (!outbounds.Any(value => (string?)value?["tag"] == "direct"))
                outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JsonObject() });
            config["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject
            {
                ["ip"] = new JsonArray("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"),
                ["outboundTag"] = "direct"
            });
        }
        foreach (var outbound in config["outbounds"]!.AsArray().OfType<JsonObject>())
        {
            if (configuration.IsLocalProxy && (string?)outbound["tag"] == "proxy" || (string?)outbound["protocol"] == "blackhole") continue;
            var stream = outbound["streamSettings"] as JsonObject;
            if (stream is null) outbound["streamSettings"] = stream = new();
            var sockopt = stream["sockopt"] as JsonObject;
            if (sockopt is null) stream["sockopt"] = sockopt = new();
            sockopt["mark"] = (string?)outbound["protocol"] == "freedom" ? LinuxRouteLease.DirectMark : LinuxRouteLease.Mark;
        }
        // Force VPN DNS before user split rules, including a selected DNS server IP.
        config["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["ip"] = new JsonArray(dns.Select(value => (JsonNode?)value).ToArray()), ["outboundTag"] = "proxy" });
        config["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject { ["network"] = "tcp,udp", ["port"] = "53", ["outboundTag"] = "proxy" });
        return config.ToJsonString();
    }

    public async Task WaitForExitAsync(CancellationToken token)
    {
        var ticks = 0;
        while (xray is { IsRunning: true } && bridge is not { IsRunning: false })
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            if (protection is not null && ++ticks % 10 == 0 && !await protection.ExistsAsync(token))
                throw new InvalidOperationException("Правила kill switch исчезли.");
        }
        throw new InvalidOperationException("Ядро VPN неожиданно завершилось.");
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action) { try { await action().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); } }
        // Cleanup ignores the caller's cancellation and runs while TUN still exists.
        if (journalWritten && routes is not null) await Attempt(() => routes.RollbackAsync(CancellationToken.None));
        if (xray is not null) await Attempt(async () => { await xray.DisposeAsync().ConfigureAwait(false); xray = null; });
        if (bridge is not null) await Attempt(async () => { await bridge.DisposeAsync().ConfigureAwait(false); bridge = null; });
        if (directory is not null && Directory.Exists(directory)) await Attempt(() => { Directory.Delete(directory, true); return Task.CompletedTask; });
        if (failures.Count > 0) throw new AggregateException("Не удалось полностью очистить сессию VPN.", failures);
        if (journalWritten && !IsProtectionActive) { File.Delete(Journal); journalWritten = false; }
    }

    public async Task ReleaseProtectionAsync()
    {
        if (protection is not null) await protection.ReleaseAsync(CancellationToken.None);
        if (journalWritten) { File.Delete(Journal); journalWritten = false; }
    }

    public static async Task<bool> RecoverAsync(string stateDirectory, ILinuxNetworkCommands commands, CancellationToken token, bool releaseProtection = true)
    {
        var journal = Path.Combine(stateDirectory, "vpn-owner.json");
        if (!File.Exists(journal)) return false;
        var bytes = await File.ReadAllBytesAsync(journal, token).ConfigureAwait(false);
        if (bytes.Length > 1024) throw new InvalidOperationException("Некорректный журнал владения.");
        var data = JsonNode.Parse(bytes)!;
        var owner = (string?)data["owner"];
        if ((int?)data["version"] is not (1 or 2) || owner is null || !Guid.TryParseExact(owner, "N", out _)) throw new InvalidOperationException("Некорректный журнал владения.");
        LinuxKillSwitch? protection = (bool?)data["killSwitch"] == true
            ? new(commands as ILinuxFirewallCommands ?? throw new InvalidOperationException(), owner) : null;
        var routes = new LinuxRouteLease(commands, "dtn" + owner[..10], owner);
        await routes.RollbackAsync(token).ConfigureAwait(false);
        await routes.RemoveOwnedLinkAsync(token).ConfigureAwait(false);
        var directory = Path.Combine(stateDirectory, "vpn-" + owner);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (protection is not null)
        {
            if (releaseProtection) await protection.ReleaseAsync(token);
            else if (await protection.ExistsAsync(token)) return true;
        }
        File.Delete(journal);
        return false;
    }
}
