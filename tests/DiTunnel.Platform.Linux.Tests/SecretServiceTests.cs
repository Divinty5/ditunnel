using System.Security.Cryptography;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Storage;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class SecretServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ditunnel-key-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "profiles.dat");
    private sealed class Runner : ISecretToolRunner
    {
        public List<(IReadOnlyList<string> Arguments, string? Input)> Calls { get; } = [];
        public Queue<SecretToolResult> Results { get; } = new();
        public string? Key;
        public Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((arguments, input));
            if (Results.Count != 0) return Task.FromResult(Results.Dequeue());
            if (arguments[0] == "store") { Key = input; return Task.FromResult(new SecretToolResult(0, "", "")); }
            return Task.FromResult(Key is null ? new SecretToolResult(1, "", "") : new SecretToolResult(0, Key, ""));
        }
    }

    [Fact]
    public async Task FirstKeyIsVerifiedAndNeverPassedInArgumentsOrWrittenToDisk()
    {
        var runner = new Runner();
        var key = await new LinuxSecretService(runner).GetProfileKeyAsync(FilePath);
        Assert.Equal(32, key.Length);
        Assert.Equal(["lookup", "store", "lookup"], runner.Calls.Select(call => call.Arguments[0]));
        Assert.Equal(Convert.ToBase64String(key), runner.Calls[1].Input);
        Assert.All(runner.Calls, call => Assert.DoesNotContain(Convert.ToBase64String(key), call.Arguments));
        Assert.False(Directory.Exists(directory));
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task MissingKeyForExistingFileIsNeverReplaced()
    {
        Directory.CreateDirectory(directory); File.WriteAllText(FilePath, "synthetic ciphertext");
        var runner = new Runner();
        await Assert.ThrowsAsync<ProfileStoreUnavailableException>(() => new LinuxSecretService(runner).GetProfileKeyAsync(FilePath));
        Assert.Single(runner.Calls);
        Assert.Equal("synthetic ciphertext", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task KeyringFailureDoesNotCreateAnotherKey()
    {
        var runner = new Runner(); runner.Results.Enqueue(new(1, "", "locked or unavailable"));
        await Assert.ThrowsAsync<ProfileStoreUnavailableException>(() => new LinuxSecretService(runner).GetProfileKeyAsync(FilePath));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("invalid base64")] [InlineData("AA==")]
    public async Task InvalidKeyDoesNotTriggerReplacement(string value)
    {
        var runner = new Runner { Key = value };
        await Assert.ThrowsAsync<ProfileStoreUnavailableException>(() => new LinuxSecretService(runner).GetProfileKeyAsync(FilePath));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task FailedKeyWritePreventsProfileStoreCreation()
    {
        var runner = new Runner(); runner.Results.Enqueue(new(1, "", "")); runner.Results.Enqueue(new(1, "", "write failed"));
        await Assert.ThrowsAsync<ProfileStoreUnavailableException>(() => new LinuxSecretService(runner).GetProfileKeyAsync(FilePath));
        Assert.Equal(2, runner.Calls.Count);
        Assert.False(File.Exists(FilePath));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
