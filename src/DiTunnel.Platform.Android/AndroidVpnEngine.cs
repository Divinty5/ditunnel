using Android.Content;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Android;

public sealed class AndroidVpnEngine(
    Context context,
    IAndroidVpnPermissionRequester permissionRequester,
    Func<SplitTunnelPolicy>? splitTunnelPolicy = null) : IProfileVpnEngine
{
    private readonly Context context = context.ApplicationContext
        ?? throw new InvalidOperationException("Android ApplicationContext недоступен.");
    private VpnStatus status = VpnStatus.Disconnected;
    private readonly Func<SplitTunnelPolicy> splitTunnelPolicy = splitTunnelPolicy ?? (() => SplitTunnelPolicy.Default);
    private readonly bool receiverRegistered = AndroidVpnServiceBridge.Register(context.ApplicationContext ?? context);

    public VpnStatus Status => status;
    public bool RequiresAdministrator => false;
    public bool IsNetworkProtectionActive => status.State == VpnConnectionState.Connected;
    public event EventHandler<VpnStatus>? StatusChanged;

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (status.State is not (VpnConnectionState.Disconnected or VpnConnectionState.Error))
            throw new InvalidOperationException("VPN уже подключается или подключён.");

        SetStatus(new(VpnConnectionState.Connecting, "Запрашиваем разрешение Android на VPN…"));
        if (!await permissionRequester.RequestAsync(cancellationToken))
        {
            SetStatus(new(VpnConnectionState.Disconnected, "Разрешение на VPN не предоставлено."));
            return;
        }

        var completion = AndroidVpnServiceBridge.ExpectStart();
        var intent = AndroidVpnServiceBridge.CreateStartIntent(context, profile, splitTunnelPolicy());
        context.StartForegroundService(intent);
        try
        {
            SetStatus(await completion.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
        }
        catch
        {
            await DisconnectAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (status.State == VpnConnectionState.Disconnected) return;
        SetStatus(new(VpnConnectionState.Disconnecting, "Останавливаем VPN…"));
        var completion = AndroidVpnServiceBridge.ExpectStop();
        var intent = new Intent(context, typeof(DiTunnelVpnService)).SetAction(DiTunnelVpnService.ActionStop);
        context.StartService(intent);
        SetStatus(await completion.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        await ConnectAsync(profile, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (status.State != VpnConnectionState.Disconnected)
            await DisconnectAsync(CancellationToken.None);
    }

    private void SetStatus(VpnStatus value)
    {
        status = value;
        StatusChanged?.Invoke(this, value);
    }
}
