using System.Security.Cryptography;
using System.Text;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Storage;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ditunnel-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "profiles.dat");
    private static readonly ImportedProfile Profile = new("Test", "VLESS", "vless://synthetic-test@192.0.2.1:443", "manual", "Test");

    [Fact]
    public void RoundTripIsEncryptedAndUsesFreshNonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var store = new LinuxProfileStore(FilePath, key);
        Assert.Empty(store.Load());
        store.Save([Profile]);
        var first = File.ReadAllBytes(FilePath);
        Assert.DoesNotContain(Profile.Content, Encoding.UTF8.GetString(first));
        Assert.Equal(Profile, Assert.Single(store.Load()));
        store.Save([Profile]);
        Assert.False(first.SequenceEqual(File.ReadAllBytes(FilePath)));
        using var reopened = new LinuxProfileStore(FilePath, key);
        Assert.Equal(Profile, Assert.Single(reopened.Load()));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public void CorruptionCannotBeOverwrittenByEmptySave()
    {
        using var store = new LinuxProfileStore(FilePath, RandomNumberGenerator.GetBytes(32));
        store.Load(); store.Save([Profile]);
        var bytes = File.ReadAllBytes(FilePath); bytes[^1] ^= 1; File.WriteAllBytes(FilePath, bytes);
        Assert.ThrowsAny<CryptographicException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save([]));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void WrongKeyPreservesExistingStore()
    {
        using (var store = new LinuxProfileStore(FilePath, RandomNumberGenerator.GetBytes(32)))
        { store.Load(); store.Save([Profile]); }
        var before = File.ReadAllBytes(FilePath);
        using var reopened = new LinuxProfileStore(FilePath, RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => reopened.Load());
        Assert.Throws<InvalidOperationException>(() => reopened.Save([]));
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(32)]
    public void InvalidFormatIsNotAnEmptyStore(int length)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(FilePath, new byte[length]);
        using var store = new LinuxProfileStore(FilePath, RandomNumberGenerator.GetBytes(32));
        Assert.ThrowsAny<CryptographicException>(() => store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save([]));
        Assert.Equal(length, new FileInfo(FilePath).Length);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
