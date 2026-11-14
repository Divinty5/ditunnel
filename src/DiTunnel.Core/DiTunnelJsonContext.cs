using System.Text.Json.Serialization;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core;

[JsonSerializable(typeof(ImportedProfile))]
[JsonSerializable(typeof(ImportedProfile[]))]
[JsonSerializable(typeof(ProfileLibrary))]
[JsonSerializable(typeof(ServerProbeResult[]))]
[JsonSerializable(typeof(ServerProbeResult))]
[JsonSerializable(typeof(ProbeDiagnostics))]
public partial class DiTunnelJsonContext : JsonSerializerContext;
