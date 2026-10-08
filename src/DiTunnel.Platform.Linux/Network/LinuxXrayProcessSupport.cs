using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Linux.Network;

public static class LinuxXrayProcessSupport
{
    public static bool IsAvailable(string runtimeDirectory)
    {
        try
        {
            var path = Path.Combine(runtimeDirectory, "XRAY-SOURCE.json");
            if (new FileInfo(path).Length > 4096) return false;
            var metadata = JsonNode.Parse(File.ReadAllBytes(path));
            var expected = (string?)metadata?["binarySha256"];
            if ((int?)metadata?["linuxProcessLookup"] != 2 || expected?.Length != 64) return false;
            using var binary = File.OpenRead(Path.Combine(runtimeDirectory, "xray"));
            return Convert.ToHexString(SHA256.HashData(binary)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
