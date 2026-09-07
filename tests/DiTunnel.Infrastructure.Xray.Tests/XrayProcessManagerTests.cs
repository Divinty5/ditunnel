namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class XrayProcessManagerTests
{
    [Fact]
    public async Task Validation_rejects_missing_configuration()
    {
        await using var manager = CreateManager();
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => manager.ValidateConfigurationAsync(missingPath));

        Assert.Equal(Path.GetFullPath(missingPath), exception.FileName);
    }

    [Fact]
    public void Constructor_rejects_nonpositive_shutdown_timeout()
    {
        var options = new XrayOptions
        {
            ExecutablePath = "xray.exe",
            WorkingDirectory = ".",
            ShutdownTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new XrayProcessManager(options));
    }

    [Fact]
    public void ConfigurationValidationIsEnabledByDefault()
    {
        var options = new XrayOptions { ExecutablePath = "xray.exe", WorkingDirectory = "." };
        Assert.True(options.ValidateConfigurationBeforeStart);
    }

    private static XrayProcessManager CreateManager()
    {
        return new XrayProcessManager(new XrayOptions
        {
            ExecutablePath = "missing-xray.exe",
            WorkingDirectory = "."
        });
    }
}
