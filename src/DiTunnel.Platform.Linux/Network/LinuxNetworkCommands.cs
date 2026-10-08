using System.Diagnostics;
using System.Runtime.Versioning;

namespace DiTunnel.Platform.Linux.Network;

public interface ILinuxNetworkCommands
{
    Task<string> IpAsync(CancellationToken token, params string[] arguments);
    Task ResolvedAsync(CancellationToken token, params string[] arguments);
}

[SupportedOSPlatform("linux")]
public sealed class LinuxNetworkCommands : ILinuxNetworkCommands, ILinuxFirewallCommands
{
    public Task<string> IpAsync(CancellationToken token, params string[] arguments) => RunAsync("/usr/sbin/ip", arguments, token);
    public async Task ResolvedAsync(CancellationToken token, params string[] arguments) =>
        _ = await RunAsync("/usr/bin/resolvectl", arguments, token).ConfigureAwait(false);

    public Task<string> ReadAsync(CancellationToken token) => RunAsync("/usr/sbin/nft", ["-j", "list", "tables"], token);
    public async Task ApplyAsync(string batch, CancellationToken token) =>
        _ = await RunAsync("/usr/sbin/nft", ["-f", "-"], token, batch).ConfigureAwait(false);

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token, string? input = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Сетевой инструмент недоступен.");
        var output = DrainAsync(process.StandardOutput, retain: true);
        var errors = DrainAsync(process.StandardError, retain: false);
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token); process.StandardInput.Close(); }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            throw;
        }
        await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Не удалось настроить собственные сетевые объекты Di-Tunnel.");
        return await output.ConfigureAwait(false);
    }

    private static async Task<string> DrainAsync(StreamReader reader, bool retain)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
            if (retain && result.Length < 1024 * 1024) result.Append(buffer, 0, Math.Min(count, 1024 * 1024 - result.Length));
        return result.ToString();
    }
}
