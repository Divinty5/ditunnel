using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Storage;

internal sealed record SecretToolResult(int ExitCode, string Output, string Error);
internal interface ISecretToolRunner
{
    Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken);
}

public sealed class LinuxSecretService
{
    public const string UnavailableMessage = "Защищённое хранилище Linux недоступно. Проверьте Secret Service и libsecret-tools, разблокируйте связку ключей и перезапустите приложение. Файл профилей не изменён.";
    private readonly ISecretToolRunner runner;
    [SupportedOSPlatform("linux")]
    public LinuxSecretService() : this(new SecretToolRunner()) { }
    internal LinuxSecretService(ISecretToolRunner runner) => this.runner = runner;

    // Called asynchronously by the entry point before constructing the ViewModel.
    // The UI instance lock must already be held to serialize first-key creation.
    public async Task<byte[]> GetProfileKeyAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(profilePath)) throw new ArgumentException("Требуется абсолютный путь.", nameof(profilePath));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var storeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profilePath)));
        string[] attributes = ["application", "ditunnel", "purpose", "profiles-v1", "store", storeId];
        try
        {
            var lookup = await runner.RunAsync(["lookup", .. attributes], null, deadline.Token);
            if (lookup.ExitCode == 0) return Decode(lookup.Output);
            // Permission/I/O errors are not a missing store. Never replace its key.
            bool exists;
            try { File.GetAttributes(profilePath); exists = true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { exists = false; }
            if (lookup.ExitCode != 1 || !string.IsNullOrWhiteSpace(lookup.Error) || exists)
                throw new ProfileStoreUnavailableException(UnavailableMessage);

            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var store = await runner.RunAsync(["store", "--label=Di-Tunnel profile encryption", .. attributes],
                    Convert.ToBase64String(key), deadline.Token);
                if (store.ExitCode != 0) throw new ProfileStoreUnavailableException(UnavailableMessage);
                var verification = await runner.RunAsync(["lookup", .. attributes], null, deadline.Token);
                if (verification.ExitCode != 0) throw new ProfileStoreUnavailableException(UnavailableMessage);
                var confirmed = Decode(verification.Output);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(key, confirmed)) throw new ProfileStoreUnavailableException(UnavailableMessage);
                }
                finally { CryptographicOperations.ZeroMemory(confirmed); }
                return key;
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProfileStoreUnavailableException) { throw; }
        catch { throw new ProfileStoreUnavailableException(UnavailableMessage); }
    }

    private static byte[] Decode(string value)
    {
        var key = Convert.FromBase64String(value.Trim());
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new ProfileStoreUnavailableException(UnavailableMessage);
    }

    [SupportedOSPlatform("linux")]
    private sealed class SecretToolRunner : ISecretToolRunner
    {
        public async Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo("/usr/bin/secret-tool")
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                // The key is carried over stdin, never argv, logs or a local key file.
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                return new(process.ExitCode, await output, await error);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
    }
}
