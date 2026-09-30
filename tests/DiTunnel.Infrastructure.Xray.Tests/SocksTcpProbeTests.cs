using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class SocksTcpProbeTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ConnectsToLiteralAddressAndReadsFragmentedReply(bool reject, bool trustCertificate)
    {
        using var key = RSA.Create(2048);
        using var certificate = TestCertificate(key);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = socket.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting, deadline.Token);
            Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
            await stream.WriteAsync(new byte[] { 5, 0 }, deadline.Token);
            var request = new byte[10];
            await stream.ReadExactlyAsync(request, deadline.Token);
            Assert.Equal(new byte[] { 5, 1, 0, 1, 192, 0, 2, 1, 1, 187 }, request);
            var response = new byte[] { 5, reject ? (byte)4 : (byte)0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var value in response.Take(reject ? 4 : response.Length)) await stream.WriteAsync(new byte[] { value }, deadline.Token);
            if (!reject)
            {
                // The proxy acknowledges CONNECT immediately, but the remote TLS
                // response arrives later. The measured result must include this delay.
                await Task.Delay(250, deadline.Token);
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); }
                catch (System.Security.Authentication.AuthenticationException) when (!trustCertificate) { }
            }
        });
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "192.0.2.1",
            CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck,
                CustomTrustStore = { certificate }
            }
        };
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = trustCertificate
            ? SocksTcpProbe.MeasureAsync(port, IPAddress.Parse("192.0.2.1"), 443, deadline.Token, options)
            : SocksTcpProbe.MeasureAsync(port, IPAddress.Parse("192.0.2.1"), 443, deadline.Token);
        if (reject) await Assert.ThrowsAsync<IOException>(() => probe);
        else if (!trustCertificate) await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(() => probe);
        else
        {
            try { Assert.InRange(await probe, 200, 5000); }
            catch { await server; throw; }
        }
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsAStalledProxy(bool connectAcknowledged)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var probe = SocksTcpProbe.MeasureAsync(((IPEndPoint)listener.LocalEndpoint).Port, IPAddress.Parse("192.0.2.1"), 443, cancellation.Token);
        using var socket = await listener.AcceptTcpClientAsync();
        var greeting = new byte[3];
        await socket.GetStream().ReadExactlyAsync(greeting);
        if (connectAcknowledged)
        {
            await socket.GetStream().WriteAsync(new byte[] { 5, 0 });
            await socket.GetStream().ReadExactlyAsync(new byte[10]);
            await socket.GetStream().WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            // Wait until the TLS ClientHello is sent before cancelling the stalled peer.
            await socket.GetStream().ReadExactlyAsync(new byte[1]);
            Assert.False(probe.IsCompleted);
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    private static X509Certificate2 TestCertificate(RSA key)
    {
        var request = new CertificateRequest("CN=AWG probe test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse("192.0.2.1"));
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel requires an imported key. No certificate is added to
        // an OS trust store; the temporary key is released with this certificate.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
    }
}
