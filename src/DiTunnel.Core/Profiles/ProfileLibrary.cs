using System.Text.Json;

namespace DiTunnel.Core.Profiles;

public sealed record EmptySubscription(string SourceId, string SourceName, string SourceUrl);

public sealed record ProfileLibrary(ImportedProfile[] Profiles, EmptySubscription[] EmptySubscriptions)
{
    public static ProfileLibrary Read(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read()) throw new JsonException("Хранилище профилей пусто.");
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var profiles = JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ImportedProfileArray)
                ?? throw new JsonException("Хранилище не содержит список профилей.");
            return new(profiles, []);
        }
        var library = JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ProfileLibrary);
        return library is { Profiles: not null, EmptySubscriptions: not null } ? library
            : throw new JsonException("Некорректный формат хранилища профилей.");
    }

    public byte[] Write() => EmptySubscriptions.Length == 0
        ? JsonSerializer.SerializeToUtf8Bytes(Profiles, DiTunnelJsonContext.Default.ImportedProfileArray)
        : JsonSerializer.SerializeToUtf8Bytes(this, DiTunnelJsonContext.Default.ProfileLibrary);
}
