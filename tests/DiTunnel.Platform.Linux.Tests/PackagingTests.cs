using DiTunnel.Linux;
using DiTunnel.Platform.Linux.Desktop;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class PackagingTests
{
    [Fact]
    public void AutostartIsIdempotentAndPreservesForeignEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            LinuxAutostart.SetEnabled(directory, true);
            var path = Path.Combine(directory, "autostart", "org.divinty5.DiTunnel.desktop");
            var content = File.ReadAllText(path);
            Assert.Contains("Exec=/usr/bin/di-tunnel --autostart", content);
            LinuxAutostart.SetEnabled(directory, true);
            Assert.Equal(content, File.ReadAllText(path));
            LinuxAutostart.SetEnabled(directory, false);
            LinuxAutostart.SetEnabled(directory, false);
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "[Desktop Entry]\nName=Foreign\n");
            Assert.Throws<IOException>(() => LinuxAutostart.SetEnabled(directory, true));
            Assert.Throws<IOException>(() => LinuxAutostart.SetEnabled(directory, false));
            Assert.Contains("Foreign", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("wrong.deb")]
    [InlineData("Di-Tunnel-0.6.0-linux-amd64.deb.sha256")]
    public void ChecksumForAnotherAssetIsRejected(string name) =>
        Assert.Throws<InvalidDataException>(() => LinuxUpdateInstaller.ParseChecksum(new string('a', 64) + "  " + name, "Di-Tunnel-0.6.0-linux-amd64.deb"));

    [Fact]
    public void NamedChecksumIsRequiredAndAmbiguousChecksumsAreRejected()
    {
        const string name = "Di-Tunnel-0.6.0-linux-amd64.deb";
        var hash = new string('a', 64);
        Assert.Equal(hash, LinuxUpdateInstaller.ParseChecksum(hash + "  " + name + "\n", name));
        Assert.Throws<InvalidDataException>(() => LinuxUpdateInstaller.ParseChecksum(hash, name));
        Assert.Throws<InvalidDataException>(() => LinuxUpdateInstaller.ParseChecksum(hash + "  " + name + "\n" + hash + "  " + name, name));
    }
}
