namespace DiTunnel.Infrastructure.Xray.Tests;

// These tests reserve and release loopback ports before starting multiple native
// cores. Do not overlap them with other runtime tests competing for those ports.
[CollectionDefinition("Xray runtime", DisableParallelization = true)]
public sealed class XrayRuntimeCollection { }
