using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DiTunnel.Infrastructure.Xray;

// One unguessable pair per probe input. Never put it in diagnostics or command lines.
public sealed class ProbeSocksCredentials
{
    public string Username { get; }
    public string Password { get; }

    private ProbeSocksCredentials()
    {
        Username = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public static ProbeSocksCredentials Create() => new();
    public override string ToString() => "ProbeSocksCredentials [redacted]";

    public WebProxy CreateProxy(int port) => new($"socks5://127.0.0.1:{port}")
    { Credentials = new NetworkCredential(Username, Password) };

    internal async Task AuthenticateAsync(Stream stream, CancellationToken token)
    {
        // Offer only RFC1929; accepting a noauth selection would fail open.
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, token).ConfigureAwait(false);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[0] != 5 || reply[1] != 2) throw new IOException("SOCKS authentication method rejected.");
        // Generated ASCII hex is bounded well below RFC1929's 255-byte fields.
        var username = Encoding.ASCII.GetBytes(Username);
        var password = Encoding.ASCII.GetBytes(Password);
        var request = new byte[3 + username.Length + password.Length];
        request[0] = 1; request[1] = checked((byte)username.Length);
        username.CopyTo(request, 2);
        request[2 + username.Length] = checked((byte)password.Length);
        password.CopyTo(request, 3 + username.Length);
        try { await stream.WriteAsync(request, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(request); CryptographicOperations.ZeroMemory(password); }
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS authentication failed.");
    }
}
