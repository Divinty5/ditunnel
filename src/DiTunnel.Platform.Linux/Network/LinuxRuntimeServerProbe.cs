using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Linux.Network;

[SupportedOSPlatform("linux")]
public sealed class LinuxRuntimeServerProbe(string runtimeDirectory, string stateDirectory, uint transportMark = 0) : IServerProbe
{
    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budget = TimeSpan.FromSeconds(mode == ServerProbeMode.Fast ? 8 : 12);
        timeout.CancelAfter(budget);
        var directory = Path.Combine(stateDirectory, "probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var xray = VerifyRuntime("xray");
            XrayProfileConfiguration configuration;
            var isAwg = AmneziaWgProfileConverter.IsAmneziaWg(profile);
            var awg = isAwg ? AmneziaWgProfileConverter.Convert(profile) : null;
            var converted = awg is null ? XrayProfileConverter.Convert(profile) : null;
            var host = awg?.ServerHost ?? converted!.ServerHost;
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault(value => value.AddressFamily == AddressFamily.InterNetworkV6)
                ?? throw new InvalidOperationException();
            // Linux AWG supports both transport families; IPv6 sockets are explicit.
            await using var bridge = awg is null ? null : await StartAwgAsync(awg, address, directory, timeout.Token, transportMark).ConfigureAwait(false);
            configuration = bridge?.Proxy ?? converted!;
            if (transportMark != 0 && !configuration.IsLocalProxy)
            {
                var outbound = (System.Text.Json.Nodes.JsonObject)configuration.Outbound.DeepClone();
                var stream = outbound["streamSettings"] as System.Text.Json.Nodes.JsonObject;
                if (stream is null) outbound["streamSettings"] = stream = new();
                var sockopt = stream["sockopt"] as System.Text.Json.Nodes.JsonObject;
                if (sockopt is null) stream["sockopt"] = sockopt = new();
                sockopt["mark"] = transportMark;
                configuration = configuration with { Outbound = outbound };
            }
            var path = Path.Combine(directory, "xray.json");
            // XrayServerProbe overwrites this existing 0600 file, preserving its mode.
            await WritePrivateAsync(path, "", timeout.Token).ConfigureAwait(false);
            var milliseconds = await XrayServerProbe.MeasureAsync(configuration, address, xray, path, timeout.Token,
                useHttps: mode == ServerProbeMode.Https, tcpOnly: awg is not null && mode == ServerProbeMode.Fast, probeTimeout: budget,
                processLifetimeFactory: static process => new LinuxProcessLifetime(process)).ConfigureAwait(false);
            return new(milliseconds, "Сервер ответил через туннель.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(null, "Время проверки сервера истекло."); }
        catch { return new(null, "Сервер не ответил через туннель. Проверьте профиль и доступность сервера."); }
        finally
        {
            // Only the fresh private directory allocated by this operation is removed.
            Directory.Delete(directory, recursive: true);
        }
    }

    internal string VerifyRuntime(string name)
    {
        var path = Path.Combine(runtimeDirectory, name);
        using var stream = File.OpenRead(path);
        var expected = File.ReadAllText(path + ".sha256").Trim().Split(' ', '\t')[0];
        if (expected.Length != 64 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма runtime не совпала.");
        return path;
    }

    internal async Task<AwgLease> StartAwgAsync(AmneziaWgProfileConfiguration configuration, IPAddress address, string directory, CancellationToken token, uint mark = 0)
    {
        var runtime = VerifyRuntime("ditunnel-awg");
        var username = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var path = Path.Combine(directory, "awg.json");
        var ready = Path.Combine(directory, "awg.ready");
        var config = System.Text.Json.Nodes.JsonNode.Parse(configuration.BuildProxyRuntimeConfiguration(address.ToString(), username, password, ready))!;
        // A host-controlled mark is applied by the Go device before opening encrypted UDP sockets.
        if (mark != 0) config["uapi"] = "fwmark=" + mark.ToString(CultureInfo.InvariantCulture) + "\n" + config["uapi"]!.GetValue<string>();
        await WritePrivateAsync(path, config.ToJsonString(), token).ConfigureAwait(false);
        var start = new ProcessStartInfo(runtime) { WorkingDirectory = runtimeDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-config"); start.ArgumentList.Add(path);
        start.ArgumentList.Add("-owner"); start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var process = Process.Start(start) ?? throw new InvalidOperationException();
        LinuxProcessLifetime? lifetime = null;
        try
        {
            lifetime = new(process);
            var errors = process.StandardError.ReadToEndAsync();
            var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null || !line.StartsWith("READY_", StringComparison.Ordinal) || !int.TryParse(line[6..], out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException();
            return new(process, lifetime, errors, configuration.CreateProxyConfiguration(port, username, password));
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            lifetime?.Dispose(); process.Dispose();
            throw;
        }
    }

    internal static async Task WritePrivateAsync(string path, string content, CancellationToken token)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.Asynchronous
        });
        var bytes = Encoding.UTF8.GetBytes(content);
        try { await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.Flush(flushToDisk: true); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal sealed class AwgLease(Process process, LinuxProcessLifetime lifetime, Task<string> errors, XrayProfileConfiguration proxy) : IAsyncDisposable
    {
        public bool IsRunning => !process.HasExited;
        public XrayProfileConfiguration Proxy { get; } = proxy;
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    try { lifetime.RequestShutdown(); }
                    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                }
                _ = await errors.ConfigureAwait(false);
            }
            finally { lifetime.Dispose(); process.Dispose(); }
        }
    }
}
