using System.Text.Json.Nodes;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Android;

internal sealed record AndroidProbeOutbound(XrayProfileConfiguration Configuration, string Address, int Port)
{
    internal ProbeSocksCredentials Credentials { get; } = ProbeSocksCredentials.Create();
}

// A separate loopback SOCKS inbound for every profile, with no cross-profile default route.
internal static class AndroidProbeBatchConfiguration
{
    internal static string Build(IReadOnlyList<AndroidProbeOutbound> profiles)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray(new JsonObject { ["tag"] = "probe-reject", ["protocol"] = "blackhole" });
        var rules = new JsonArray();
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var source = JsonNode.Parse(profile.Configuration.Build(profile.Address, false, profile.Port, enableSocksUdp: false, proxyCredentials: profile.Credentials))!;
            var inboundTag = $"probe-{index}";
            var tags = source["outbounds"]!.AsArray().ToDictionary(node => node!["tag"]!.GetValue<string>(),
                node => $"{inboundTag}-{node!["tag"]!.GetValue<string>()}", StringComparer.Ordinal);
            var inbound = source["inbounds"]![0]!.DeepClone();
            inbound["tag"] = inboundTag;
            inbounds.Add(inbound);
            foreach (var item in source["outbounds"]!.AsArray())
            {
                var outbound = item!.DeepClone();
                outbound["tag"] = tags[outbound["tag"]!.GetValue<string>()];
                outbounds.Add(outbound);
            }
            foreach (var item in source["routing"]!["rules"]!.AsArray())
            {
                var rule = item!.DeepClone();
                rule["inboundTag"] = new JsonArray(inboundTag);
                rule["outboundTag"] = tags[rule["outboundTag"]!.GetValue<string>()];
                rules.Add(rule);
            }
            rules.Add(new JsonObject { ["inboundTag"] = new JsonArray(inboundTag), ["outboundTag"] = tags["proxy"] });
        }
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["inbounds"] = inbounds, ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules }
        }.ToJsonString();
    }
}
