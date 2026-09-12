using System.Text.Json.Serialization;

namespace DiTunnel.App;

[JsonSerializable(typeof(UserSettings))]
internal partial class AppJsonContext : JsonSerializerContext;
