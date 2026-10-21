using System.Text;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxCountryResolverTests
{
    [Fact]
    public async Task Local_database_resolves_literal_server_address_without_external_geoip_service()
    {
        var path = Path.GetTempFileName();
        try
        {
            // geoip.dat protobuf: SE, 192.0.2.0/24.
            File.WriteAllBytes(path, [10, 14, 18, 8, 10, 4, 192, 0, 2, 0, 16, 24, 10, 2, .. Encoding.ASCII.GetBytes("SE")]);
            var resolver = new LinuxServerCountryResolver(path);
            var profile = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@192.0.2.10:443?security=none").Single();
            Assert.Equal("SE", await resolver.ResolveAsync(profile));
            Assert.Equal("SE", await resolver.ResolveAsync(profile));
        }
        finally { File.Delete(path); }
    }
}
