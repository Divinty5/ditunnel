using System.ComponentModel;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DiTunnel.Core.Connection;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace DiTunnel.Platform.Windows;

/// <summary>
/// Owns Di-Tunnel's persistent WFP provider, sublayer and filters. Persistent filters make an
/// unexpected UI/host crash fail closed; the next elevated process removes the deterministic
/// stale rule set before installing a replacement.
/// </summary>
public sealed class WindowsKillSwitchController : INetworkProtectionController
{
    internal static readonly Guid ProviderKey = new("cd2f9a42-8c0e-4e34-bdb5-7422a5bb02d7");
    internal static readonly Guid SubLayerKey = new("23d553fa-b668-43f5-8a64-8afed28c135f");
    private const uint FwpEFilterNotFound = 0x80320003;
    private const uint FwpEProviderNotFound = 0x80320005;
    private const uint FwpESubLayerNotFound = 0x80320007;
    private const int MaximumServerAddressesPerFamily = 16;
    private const int MaximumDirectAddressesPerFamily = 64;
    private static readonly Guid TunnelV4Key = new("7412847c-a1e3-42a9-859f-a8435da4c402");
    private static readonly Guid TunnelV6Key = new("75cb1699-5c40-4e6e-99d5-a03666ae09a2");
    private static readonly Guid TunnelAddressV4Key = new("c01e106e-f9f4-4f0d-a442-9168903dc41a");
    private static readonly Guid TunnelAddressV6Key = new("d1083184-0f65-4c9e-8442-4710d4c6d72f");
    private static readonly Guid LoopbackV4Key = new("8957c15a-f422-4f39-81ba-a57e5720c601");
    private static readonly Guid LoopbackV6Key = new("41d253b3-3fc2-4ed2-a302-e4927a6216f0");
    private static readonly Guid DhcpV4Key = new("b8a3ab0c-b73d-43ac-93a1-96b1565e0061");
    private static readonly Guid DhcpV6Key = new("4e8e448f-1f5c-4272-8dcf-fbf295e71e64");
    private static readonly Guid BlockV4Key = new("50275fac-1177-46b4-a839-04254fe37685");
    private static readonly Guid BlockV6Key = new("6438a235-b53c-4303-b19e-e8af06a06040");
    private static readonly Guid ProbeV4Key = new("88b4e719-bb96-47ef-96b2-ec79c1176ec1");
    private static readonly Guid ProbeV6Key = new("48e62582-572c-430c-94d6-b47a991e10d8");
    private static readonly (Guid Key, string Prefix)[] LanV4 =
    [
        (new("20cc9f3b-c4db-4bd3-a05f-e943b330b328"), "10.0.0.0/8"),
        (new("99578fae-9826-4f5e-af1d-a6e9018141a3"), "172.16.0.0/12"),
        (new("93ecb3c9-39f7-4aba-8865-a663202ab1ce"), "192.168.0.0/16"),
        (new("1a205f08-a775-4841-9359-49cc315881f2"), "169.254.0.0/16")
    ];
    private static readonly (Guid Key, string Prefix)[] LanV6 =
    [
        (new("1a0f286d-a11e-4287-bf78-a07f3b67463c"), "fc00::/7"),
        (new("0f542518-5411-4dc7-ae88-8de749ae92f7"), "fe80::/10")
    ];

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool installed;
    private ulong activeTunnelLuid;
    private KillSwitchConfiguration? activeConfiguration;
    public NetworkProtectionStatus Status { get; private set; } = NetworkProtectionStatus.Inactive;
    public event EventHandler<NetworkProtectionStatus>? StatusChanged;

    public Task ActivateAsync(KillSwitchConfiguration configuration, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A Windows tunnel interface index is required.");

    public async Task ActivateAsync(KillSwitchConfiguration configuration, uint tunnelInterfaceIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await gate.WaitAsync(cancellationToken);
        try
        {
            SetStatus(NetworkProtectionState.Activating, "Устанавливаем правила блокировки WFP…");
            cancellationToken.ThrowIfCancellationRequested();
            activeTunnelLuid = ConvertInterfaceIndexToLuid(tunnelInterfaceIndex);
            Install(configuration, activeTunnelLuid);
            installed = true;
            activeConfiguration = configuration;
            SetStatus(NetworkProtectionState.Active, "Kill switch активен.");
        }
        catch (Exception error)
        {
            try { RemoveOwnedObjects(); } catch { }
            SetStatus(NetworkProtectionState.Faulted, $"Не удалось включить kill switch: {error.Message}");
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            SetStatus(NetworkProtectionState.Deactivating, "Удаляем правила блокировки WFP…");
            cancellationToken.ThrowIfCancellationRequested();
            RemoveOwnedObjects();
            installed = false;
            activeTunnelLuid = 0;
            activeConfiguration = null;
            SetStatus(NetworkProtectionState.Inactive, "Kill switch выключен.");
        }
        catch (Exception error)
        {
            SetStatus(NetworkProtectionState.Faulted, $"Не удалось удалить правила kill switch: {error.Message}");
            throw;
        }
        finally { gate.Release(); }
    }

    public static void CleanupStaleFilters() => RemoveOwnedObjects();

    public async Task PrepareTransitionAsync(KillSwitchConfiguration nextConfiguration, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!installed || activeTunnelLuid == 0) throw new InvalidOperationException("Kill switch is not active.");
            Install(nextConfiguration, activeTunnelLuid);
            installed = true;
            activeConfiguration = nextConfiguration;
        }
        finally { gate.Release(); }
    }

    public async Task AddDirectAddressesAsync(IEnumerable<IPAddress> addresses, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!installed || activeTunnelLuid == 0 || activeConfiguration is null) return;
            var merged = (activeConfiguration.DirectAddresses ?? []).Concat(addresses)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Distinct().ToArray();
            if (merged.SequenceEqual(activeConfiguration.DirectAddresses ?? [])) return;
            var updated = activeConfiguration with { DirectAddresses = merged };
            Install(updated, activeTunnelLuid);
            activeConfiguration = updated;
        }
        finally { gate.Release(); }
    }

    public async Task<IAsyncDisposable> PermitProbeEndpointAsync(IPAddress address, ushort port, KillSwitchTransportProtocol transport, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!installed) return ProbePermitLease.Empty;
            var key = address.AddressFamily == AddressFamily.InterNetwork ? ProbeV4Key : ProbeV6Key;
            var layer = address.AddressFamily == AddressFamily.InterNetwork ? PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4 : PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6;
            var configuration = KillSwitchConfiguration.Create([address], port, transport, false);
            WfpEngineTransaction.Execute(engine =>
            {
                DeleteFilter(engine, key);
                AddServerFilter(engine, key, "Permit temporary Di-Tunnel server probe", layer, address, configuration);
            });
            return new ProbePermitLease(this, key);
        }
        finally { gate.Release(); }
    }

    private async ValueTask RemoveProbePermitAsync(Guid key)
    {
        await gate.WaitAsync();
        try
        {
            if (!installed) return;
            WfpEngineTransaction.Execute(engine =>
            {
                DeleteFilter(engine, key);
            });
        }
        finally { gate.Release(); }
    }

    private sealed class ProbePermitLease(WindowsKillSwitchController? owner, Guid key) : IAsyncDisposable
    {
        internal static readonly ProbePermitLease Empty = new(null, Guid.Empty);
        private WindowsKillSwitchController? currentOwner = owner;
        public async ValueTask DisposeAsync()
        {
            var value = Interlocked.Exchange(ref currentOwner, null);
            if (value is not null) await value.RemoveProbePermitAsync(key);
        }
    }

    private static unsafe ulong ConvertInterfaceIndexToLuid(uint interfaceIndex)
    {
        NET_LUID_LH luid = default;
        var result = PInvoke.ConvertInterfaceIndexToLuid(interfaceIndex, &luid);
        if (result != 0) throw new Win32Exception((int)result);
        return luid.Value;
    }

    private static unsafe void Install(KillSwitchConfiguration configuration, ulong tunnelLuid)
    {
        var v4 = configuration.ServerAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(MaximumServerAddressesPerFamily).ToArray();
        var v6 = configuration.ServerAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(MaximumServerAddressesPerFamily).ToArray();
        if (v4.Length + v6.Length != configuration.ServerAddresses.Count)
            throw new ArgumentException($"Kill switch supports at most {MaximumServerAddressesPerFamily} server addresses per IP family.", nameof(configuration));

        WfpEngineTransaction.Execute(engine =>
        {
            RemoveOwnedObjects(engine);
            AddProvider(engine);
            AddSubLayer(engine);
            for (var i = 0; i < v4.Length; i++) AddServerFilter(engine, ServerFilterKey(false, i), $"Permit VPN server IPv4 #{i + 1}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, v4[i], configuration);
            for (var i = 0; i < v6.Length; i++) AddServerFilter(engine, ServerFilterKey(true, i), $"Permit VPN server IPv6 #{i + 1}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, v6[i], configuration);
            var directV4 = (configuration.DirectAddresses ?? []).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(MaximumDirectAddressesPerFamily).ToArray();
            var directV6 = (configuration.DirectAddresses ?? []).Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(MaximumDirectAddressesPerFamily).ToArray();
            if (directV4.Length + directV6.Length != (configuration.DirectAddresses?.Count ?? 0))
                throw new ArgumentException($"Kill switch supports at most {MaximumDirectAddressesPerFamily} direct addresses per IP family.", nameof(configuration));
            for (var i = 0; i < directV4.Length; i++) AddAddressFilter(engine, DirectFilterKey(false, i), $"Permit selected direct IPv4 #{i + 1}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, directV4[i], 32, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 14);
            for (var i = 0; i < directV6.Length; i++) AddAddressFilter(engine, DirectFilterKey(true, i), $"Permit selected direct IPv6 #{i + 1}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, directV6[i], 128, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 14);
            AddInterfaceFilter(engine, TunnelV4Key, "Permit Di-Tunnel interface IPv4", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, tunnelLuid);
            AddInterfaceFilter(engine, TunnelV6Key, "Permit Di-Tunnel interface IPv6", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, tunnelLuid);
            AddLocalAddressFilter(engine, TunnelAddressV4Key, "Permit Di-Tunnel source IPv4", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, IPAddress.Parse("172.31.255.1"), 32);
            AddLocalAddressFilter(engine, TunnelAddressV6Key, "Permit Di-Tunnel source IPv6", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, IPAddress.Parse("fd52:d17::1"), 128);
            AddAddressFilter(engine, LoopbackV4Key, "Permit loopback IPv4", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, IPAddress.Loopback, 32, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
            AddAddressFilter(engine, LoopbackV6Key, "Permit loopback IPv6", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, IPAddress.IPv6Loopback, 128, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
            AddDhcpFilter(engine, DhcpV4Key, "Permit DHCP IPv4", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, 68, 67);
            AddDhcpFilter(engine, DhcpV6Key, "Permit DHCP IPv6", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, 546, 547);
            if (configuration.AllowLocalNetwork)
            {
                foreach (var (key, prefix) in LanV4) AddPrefixFilter(engine, key, $"Permit LAN {prefix}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4, prefix);
                foreach (var (key, prefix) in LanV6) AddPrefixFilter(engine, key, $"Permit LAN {prefix}", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6, prefix);
            }
            AddTerminalBlock(engine, BlockV4Key, "Block non-VPN IPv4", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
            AddTerminalBlock(engine, BlockV6Key, "Block non-VPN IPv6", PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
        });
    }

    private static unsafe void AddProvider(FWPM_ENGINE_HANDLE engine)
    {
        const string name = "Di-Tunnel Kill Switch";
        const string description = "Persistent network protection owned by Di-Tunnel";
        fixed (char* namePointer = name)
        fixed (char* descriptionPointer = description)
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = ProviderKey,
                displayData = new() { name = new PWSTR(namePointer), description = new PWSTR(descriptionPointer) },
                flags = 1
            };
            WfpEngineTransaction.ThrowIfFailed(PInvoke.FwpmProviderAdd0(engine, &provider, default));
        }
    }

    private static unsafe void AddSubLayer(FWPM_ENGINE_HANDLE engine)
    {
        const string name = "Di-Tunnel Kill Switch Rules";
        const string description = "High-priority permit exceptions and terminal blocks";
        var providerKey = ProviderKey;
        fixed (char* namePointer = name)
        fixed (char* descriptionPointer = description)
        {
            var subLayer = new FWPM_SUBLAYER0
            {
                subLayerKey = SubLayerKey,
                displayData = new() { name = new PWSTR(namePointer), description = new PWSTR(descriptionPointer) },
                flags = 1,
                providerKey = &providerKey,
                weight = ushort.MaxValue - 1
            };
            WfpEngineTransaction.ThrowIfFailed(PInvoke.FwpmSubLayerAdd0(engine, &subLayer, default));
        }
    }

    private static unsafe void AddInterfaceFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, ulong luid)
    {
        var condition = EqualUInt64(PInvoke.FWPM_CONDITION_IP_LOCAL_INTERFACE, &luid);
        AddFilter(engine, key, name, layer, &condition, 1, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
    }

    private static unsafe void AddServerFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, IPAddress address, KillSwitchConfiguration configuration)
    {
        var conditions = stackalloc FWPM_FILTER_CONDITION0[3];
        var count = 0;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addressMask = new FWP_V4_ADDR_AND_MASK { addr = ToHostOrderIpv4(address), mask = uint.MaxValue };
            conditions[count++] = EqualV4Mask(&addressMask);
        }
        else
        {
            var addressMask = new FWP_V6_ADDR_AND_MASK { prefixLength = 128 };
            var bytes = address.GetAddressBytes();
            fixed (byte* source = bytes) Buffer.MemoryCopy(source, &addressMask.addr, 16, 16);
            conditions[count++] = EqualV6Mask(&addressMask);
        }
        if (configuration.ServerPort is { } port) conditions[count++] = EqualUInt16(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, port);
        if (configuration.ServerTransport is KillSwitchTransportProtocol.Tcp or KillSwitchTransportProtocol.Udp)
            conditions[count++] = EqualUInt8(PInvoke.FWPM_CONDITION_IP_PROTOCOL, configuration.ServerTransport == KillSwitchTransportProtocol.Tcp ? (byte)6 : (byte)17);
        AddFilter(engine, key, name, layer, conditions, (uint)count, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
    }

    private static unsafe void AddDhcpFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, ushort localPort, ushort remotePort)
    {
        var conditions = stackalloc FWPM_FILTER_CONDITION0[3];
        conditions[0] = EqualUInt8(PInvoke.FWPM_CONDITION_IP_PROTOCOL, 17);
        conditions[1] = EqualUInt16(PInvoke.FWPM_CONDITION_IP_LOCAL_PORT, localPort);
        conditions[2] = EqualUInt16(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, remotePort);
        AddFilter(engine, key, name, layer, conditions, 3, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
    }

    private static unsafe void AddPrefixFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, string prefix)
    {
        var parts = prefix.Split('/');
        AddAddressFilter(engine, key, name, layer, IPAddress.Parse(parts[0]), byte.Parse(parts[1]), FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
    }

    private static unsafe void AddAddressFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, IPAddress address, byte prefixLength, FWP_ACTION_TYPE action, byte weight)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bits = ToHostOrderIpv4(address);
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            var addressMask = new FWP_V4_ADDR_AND_MASK { addr = bits, mask = mask };
            var condition = EqualV4Mask(&addressMask);
            AddFilter(engine, key, name, layer, &condition, 1, action, weight);
            return;
        }
        var addressMaskV6 = new FWP_V6_ADDR_AND_MASK { prefixLength = prefixLength };
        var bytes = address.GetAddressBytes();
        fixed (byte* source = bytes) Buffer.MemoryCopy(source, &addressMaskV6.addr, 16, 16);
        var conditionV6 = EqualV6Mask(&addressMaskV6);
        AddFilter(engine, key, name, layer, &conditionV6, 1, action, weight);
    }

    private static unsafe void AddLocalAddressFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, IPAddress address, byte prefixLength)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addressMask = new FWP_V4_ADDR_AND_MASK { addr = ToHostOrderIpv4(address), mask = uint.MaxValue };
            var condition = EqualV4Mask(PInvoke.FWPM_CONDITION_IP_LOCAL_ADDRESS, &addressMask);
            AddFilter(engine, key, name, layer, &condition, 1, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
            return;
        }
        var addressMaskV6 = new FWP_V6_ADDR_AND_MASK { prefixLength = prefixLength };
        var bytes = address.GetAddressBytes();
        fixed (byte* source = bytes) Buffer.MemoryCopy(source, &addressMaskV6.addr, 16, 16);
        var conditionV6 = EqualV6Mask(PInvoke.FWPM_CONDITION_IP_LOCAL_ADDRESS, &addressMaskV6);
        AddFilter(engine, key, name, layer, &conditionV6, 1, FWP_ACTION_TYPE.FWP_ACTION_PERMIT, 15);
    }

    internal static uint ToHostOrderIpv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("An IPv4 address is required.", nameof(address));
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }

    private static unsafe void AddTerminalBlock(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer) =>
        AddFilter(engine, key, name, layer, null, 0, FWP_ACTION_TYPE.FWP_ACTION_BLOCK, 0);

    private static unsafe void AddFilter(FWPM_ENGINE_HANDLE engine, Guid key, string name, Guid layer, FWPM_FILTER_CONDITION0* conditions, uint conditionCount, FWP_ACTION_TYPE action, byte weight)
    {
        const string description = "Di-Tunnel managed WFP filter";
        var providerKey = ProviderKey;
        fixed (char* namePointer = name)
        fixed (char* descriptionPointer = description)
        {
            var filter = new FWPM_FILTER0
            {
                filterKey = key,
                displayData = new() { name = new PWSTR(namePointer), description = new PWSTR(descriptionPointer) },
                flags = FWPM_FILTER_FLAGS.FWPM_FILTER_FLAG_PERSISTENT,
                providerKey = &providerKey,
                layerKey = layer,
                subLayerKey = SubLayerKey,
                weight = new() { type = FWP_DATA_TYPE.FWP_UINT8, Anonymous = new() { uint8 = weight } },
                numFilterConditions = conditionCount,
                filterCondition = conditions,
                action = new() { type = action }
            };
            WfpEngineTransaction.ThrowIfFailed(PInvoke.FwpmFilterAdd0(engine, &filter, default, null));
        }
    }

    private static unsafe FWPM_FILTER_CONDITION0 EqualUInt8(Guid field, byte value) => new()
    {
        fieldKey = field, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new() { type = FWP_DATA_TYPE.FWP_UINT8, Anonymous = new() { uint8 = value } }
    };
    private static unsafe FWPM_FILTER_CONDITION0 EqualUInt16(Guid field, ushort value) => new()
    {
        fieldKey = field, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new() { type = FWP_DATA_TYPE.FWP_UINT16, Anonymous = new() { uint16 = value } }
    };
    private static unsafe FWPM_FILTER_CONDITION0 EqualUInt64(Guid field, ulong* value) => new()
    {
        fieldKey = field, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new() { type = FWP_DATA_TYPE.FWP_UINT64, Anonymous = new() { uint64 = value } }
    };
    private static unsafe FWPM_FILTER_CONDITION0 EqualV4Mask(FWP_V4_ADDR_AND_MASK* value) => EqualV4Mask(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, value);
    private static unsafe FWPM_FILTER_CONDITION0 EqualV4Mask(Guid field, FWP_V4_ADDR_AND_MASK* value) => new()
    {
        fieldKey = field, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new() { type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK, Anonymous = new() { v4AddrMask = value } }
    };
    private static unsafe FWPM_FILTER_CONDITION0 EqualV6Mask(FWP_V6_ADDR_AND_MASK* value) => EqualV6Mask(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, value);
    private static unsafe FWPM_FILTER_CONDITION0 EqualV6Mask(Guid field, FWP_V6_ADDR_AND_MASK* value) => new()
    {
        fieldKey = field, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
        conditionValue = new() { type = FWP_DATA_TYPE.FWP_V6_ADDR_MASK, Anonymous = new() { v6AddrMask = value } }
    };

    private static void RemoveOwnedObjects() => WfpEngineTransaction.Execute(RemoveOwnedObjects);

    private static unsafe void RemoveOwnedObjects(FWPM_ENGINE_HANDLE engine)
    {
        foreach (var key in AllFilterKeys()) IgnoreNotFound(PInvoke.FwpmFilterDeleteByKey0(engine, &key));
        var subLayer = SubLayerKey;
        IgnoreNotFound(PInvoke.FwpmSubLayerDeleteByKey0(engine, &subLayer));
        var provider = ProviderKey;
        IgnoreNotFound(PInvoke.FwpmProviderDeleteByKey0(engine, &provider));
    }

    private static IEnumerable<Guid> AllFilterKeys()
    {
        yield return TunnelV4Key; yield return TunnelV6Key; yield return TunnelAddressV4Key; yield return TunnelAddressV6Key;
        yield return LoopbackV4Key; yield return LoopbackV6Key;
        yield return DhcpV4Key; yield return DhcpV6Key; yield return BlockV4Key; yield return BlockV6Key;
        yield return ProbeV4Key; yield return ProbeV6Key;
        foreach (var entry in LanV4) yield return entry.Key;
        foreach (var entry in LanV6) yield return entry.Key;
        for (var i = 0; i < MaximumServerAddressesPerFamily; i++) { yield return ServerFilterKey(false, i); yield return ServerFilterKey(true, i); }
        for (var i = 0; i < MaximumDirectAddressesPerFamily; i++) { yield return DirectFilterKey(false, i); yield return DirectFilterKey(true, i); }
    }

    private static Guid ServerFilterKey(bool ipv6, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        (ipv6 ? new Guid("0ed922d8-1256-484c-9c48-200557216e00") : new Guid("53be24a6-cf15-47c6-a945-bcc013571e00")).TryWriteBytes(bytes);
        bytes[15] = checked((byte)index);
        return new Guid(bytes);
    }

    private static Guid DirectFilterKey(bool ipv6, int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        (ipv6 ? new Guid("fc0fe414-0f20-4ffd-bdf0-ac5c3eb70e00") : new Guid("c29144d3-af18-4dd7-938c-5ea708b40e00")).TryWriteBytes(bytes);
        bytes[15] = checked((byte)index);
        return new Guid(bytes);
    }

    private static void IgnoreNotFound(uint result)
    {
        if (result is 0 or FwpEFilterNotFound or FwpEProviderNotFound or FwpESubLayerNotFound) return;
        WfpEngineTransaction.ThrowIfFailed(result);
    }

    private static unsafe void DeleteFilter(FWPM_ENGINE_HANDLE engine, Guid key) =>
        IgnoreNotFound(PInvoke.FwpmFilterDeleteByKey0(engine, &key));

    private void SetStatus(NetworkProtectionState state, string message)
    {
        Status = new(state, message);
        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        await DeactivateAsync();
        gate.Dispose();
    }
}
