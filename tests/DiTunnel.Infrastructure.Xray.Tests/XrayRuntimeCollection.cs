using System.Text.Json.Nodes;

namespace DiTunnel.Infrastructure.Xray.Tests;

// These tests reserve and release loopback ports before starting multiple native
// cores. Do not overlap them with other runtime tests competing for those ports.
[CollectionDefinition("Xray runtime", DisableParallelization = true)]
public sealed class XrayRuntimeCollection
{
    internal static JsonObject AllowTestHttpEndpoint(int port, bool redirect = false)
    {
        // Recent cores protect private destinations behind proxy inbounds by default.
        var settings = new JsonObject
        {
            ["finalRules"] = new JsonArray(new JsonObject
            {
                ["action"] = "allow", ["network"] = "tcp", ["ip"] = new JsonArray("127.0.0.1/32"),
                ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
        };
        if (redirect) settings["redirect"] = $"127.0.0.1:{port}";
        return settings;
    }
}
