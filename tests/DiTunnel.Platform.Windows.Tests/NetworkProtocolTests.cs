using System.Buffers.Binary;
using System.IO.Pipes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class NetworkProtocolTests
{
    [Fact]
    public async Task RoundTripPreservesProfilesAndPoliciesAcrossFragmentedReads()
    {
        var request = new NetworkRequest(43, "connect", new ImportedProfile("Профиль", "Trojan", "trojan://secret@example.org:443"),
            new(SplitTunnelMode.BypassSelected, ["example.org"], []), new(true, false, false, false), true, true);
        using var stream = new MemoryStream();
        await NetworkProtocol.WriteAsync(stream, request);
        stream.Position = 0;
        using var fragmented = new FragmentedStream(stream);
        var decoded = await NetworkProtocol.ReadAsync<NetworkRequest>(fragmented);
        Assert.Equal(request.Profile, decoded!.Profile);
        Assert.Equal(request.Split!.Domains, decoded.Split!.Domains);
        Assert.Equal(request.Connection, decoded.Connection);
        Assert.True(decoded.BlockAds && decoded.StrictAds);
        Assert.Null(await NetworkProtocol.ReadAsync<NetworkRequest>(fragmented));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(NetworkProtocol.MaximumFrame + 1)]
    public async Task InvalidLengthIsRejectedBeforeAllocatingPayload(int size)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, size);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => NetworkProtocol.ReadAsync<NetworkRequest>(stream));
    }

    [Fact]
    public async Task TruncatedMessageCannotBeUsedAsARequest()
    {
        using var stream = new MemoryStream();
        await NetworkProtocol.WriteAsync(stream, new NetworkRequest(1, "disconnect"));
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => NetworkProtocol.ReadAsync<NetworkRequest>(stream));
    }

    [Fact]
    public async Task PipePeerMustHaveTheExpectedProcessId()
    {
        // Both ends are test-owned; no elevated helper is started.
        var name = "DiTunnel.Test." + Guid.NewGuid().ToString("N");
        using var namedServer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepting = namedServer.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepting;
        NetworkPeer.Check(namedServer, Environment.ProcessId, false);
        NetworkPeer.Check(client, Environment.ProcessId, true);
        Assert.Throws<InvalidOperationException>(() => NetworkPeer.Check(namedServer, int.MaxValue, false));
    }

    private sealed class FragmentedStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
