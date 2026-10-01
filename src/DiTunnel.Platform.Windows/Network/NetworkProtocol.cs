using System.Buffers.Binary;
using System.Text.Json;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Windows.Network;

internal sealed record NetworkRequest(long Id, string Operation, ImportedProfile? Profile = null,
    SplitTunnelPolicy? Split = null, ConnectionPolicy? Connection = null, bool BlockAds = false,
    bool StrictAds = false, ServerProbeMode ProbeMode = ServerProbeMode.Fast);
internal sealed record NetworkReply(long Id, VpnStatus? Status = null, bool ProtectionActive = false,
    ServerProbeResult? Probe = null, string? Error = null);

/// <summary>Length-bounded frames keep credentials off command lines and diagnostic logs.</summary>
internal static class NetworkProtocol
{
    internal const int MaximumFrame = 2 * 1024 * 1024;

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumFrame) throw new InvalidOperationException("Профиль слишком большой для сетевого модуля.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }

    internal static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken token = default) where T : class
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrame) throw new InvalidDataException("Некорректный размер сообщения сетевого модуля.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("Пустое сообщение сетевого модуля.");
    }
}
