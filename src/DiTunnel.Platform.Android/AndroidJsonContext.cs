using System.Text.Json.Serialization;

namespace DiTunnel.Platform.Android;

[JsonSerializable(typeof(AndroidVpnServiceBridge.ServiceRequest))]
internal partial class AndroidJsonContext : JsonSerializerContext;
