using System.Text.Json.Nodes;
using System.Diagnostics;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Android;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class AndroidProbeBatchConfigurationTests
{
    [Fact]
    public async Task ActualXrayAcceptsScopedMultiProfileConfiguration()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "xray", "26.3.27", "windows-x64", "xray.exe");
        Assert.True(File.Exists(runtime));
        var profile = Profile("00000000-0000-0000-0000-000000000001", "192.0.2.1");
        var path = Path.Combine(Path.GetTempPath(), $"ditunnel-probe-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, AndroidProbeBatchConfiguration.Build(
                [new(profile, "192.0.2.1", 31001), new(profile with { SupportsIpv6 = false }, "192.0.2.2", 31002)]));
            var start = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "run", "-test", "-config", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(); }
            Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
        }
        finally { File.Delete(path); }
    }

    private static XrayProfileConfiguration Profile(string id, string host) =>
        XrayProfileConverter.Convert(new ImportedProfile("Synthetic", "VLESS", $"vless://{id}@{host}:443?security=none"));

    [Fact]
    public void CredentialsAreDistinctPerProfileAndSurviveValidationAndSubdivision()
    {
        var profile = Profile("00000000-0000-0000-0000-000000000001", "192.0.2.1");
        var first = new AndroidProbeOutbound(profile, "192.0.2.1", 31001);
        var second = new AndroidProbeOutbound(profile, "192.0.2.1", 31002);
        var validated = JsonNode.Parse(AndroidProbeBatchConfiguration.Build([second]))!;
        var batch = JsonNode.Parse(AndroidProbeBatchConfiguration.Build([first, second]))!;
        var retry = JsonNode.Parse(AndroidProbeBatchConfiguration.Build([second]))!;
        var secondSettings = batch["inbounds"]![1]!["settings"]!;
        Assert.True(JsonNode.DeepEquals(validated["inbounds"]![0]!["settings"], secondSettings));
        Assert.True(JsonNode.DeepEquals(retry["inbounds"]![0]!["settings"], secondSettings));
        Assert.Equal("password", secondSettings["auth"]!.GetValue<string>());
        Assert.NotEqual(first.Credentials.Username, second.Credentials.Username);
        Assert.NotEqual(first.Credentials.Password, second.Credentials.Password);
        Assert.DoesNotContain(second.Credentials.Password, second.ToString());
    }

    [Fact]
    public void EveryInboundHasItsOwnOutboundAndNoPeerOrDirectDefaultRoute()
    {
        var first = Profile("00000000-0000-0000-0000-000000000001", "192.0.2.1");
        var second = Profile("00000000-0000-0000-0000-000000000002", "192.0.2.2");
        var root = JsonNode.Parse(AndroidProbeBatchConfiguration.Build(
            [new(first, "192.0.2.1", 31001), new(second, "192.0.2.2", 31002)]))!;
        var inbounds = root["inbounds"]!.AsArray();
        var outbounds = root["outbounds"]!.AsArray();
        Assert.Equal(2, inbounds.Count);
        Assert.Equal("blackhole", outbounds[0]!["protocol"]!.GetValue<string>());
        Assert.DoesNotContain(outbounds, outbound => outbound!["protocol"]!.GetValue<string>() == "freedom");
        for (var index = 0; index < inbounds.Count; index++)
        {
            Assert.Equal("127.0.0.1", inbounds[index]!["listen"]!.GetValue<string>());
            Assert.False(inbounds[index]!["settings"]!["udp"]!.GetValue<bool>());
            var tag = inbounds[index]!["tag"]!.GetValue<string>();
            var route = Assert.Single(root["routing"]!["rules"]!.AsArray(), rule =>
                rule!["inboundTag"]![0]!.GetValue<string>() == tag);
            Assert.Equal($"probe-{index}-proxy", route!["outboundTag"]!.GetValue<string>());
            var outbound = Assert.Single(outbounds, item => item!["tag"]!.GetValue<string>() == $"probe-{index}-proxy");
            Assert.Equal($"192.0.2.{index + 1}", outbound!["settings"]!["vnext"]![0]!["address"]!.GetValue<string>());
        }
    }

    [Fact]
    public void AddressFamilyRestrictionsAreScopedToTheirOwnProfile()
    {
        var profile = Profile("00000000-0000-0000-0000-000000000001", "192.0.2.1");
        var root = JsonNode.Parse(AndroidProbeBatchConfiguration.Build(
            [new(profile with { SupportsIpv4 = false }, "192.0.2.1", 31001), new(profile, "192.0.2.2", 31002)]))!;
        var blockRule = Assert.Single(root["routing"]!["rules"]!.AsArray(), rule => rule!["ip"] is not null);
        Assert.Equal("probe-0", blockRule!["inboundTag"]![0]!.GetValue<string>());
        Assert.Equal("probe-0-block", blockRule["outboundTag"]!.GetValue<string>());
        Assert.DoesNotContain(root["routing"]!["rules"]!.AsArray(), rule =>
            rule!["inboundTag"]![0]!.GetValue<string>() == "probe-1" && rule["outboundTag"]!.GetValue<string>().Contains("block"));
    }
}
