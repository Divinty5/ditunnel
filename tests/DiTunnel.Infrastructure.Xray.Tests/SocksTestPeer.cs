using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

internal static class SocksTestPeer
{
    internal static async Task AuthenticateAsync(Stream stream, ProbeSocksCredentials credentials, CancellationToken token)
    {
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, token);
        Assert.Equal(new byte[] { 5, 1, 2 }, greeting);
        foreach (var value in new byte[] { 5, 2 }) await stream.WriteAsync(new byte[] { value }, token);
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token);
        Assert.Equal(1, header[0]);
        var username = new byte[header[1]];
        await stream.ReadExactlyAsync(username, token);
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token);
        var password = new byte[length[0]];
        await stream.ReadExactlyAsync(password, token);
        Assert.Equal(credentials.Username, Encoding.ASCII.GetString(username));
        Assert.Equal(credentials.Password, Encoding.ASCII.GetString(password));
        foreach (var value in new byte[] { 1, 0 }) await stream.WriteAsync(new byte[] { value }, token);
    }
}
