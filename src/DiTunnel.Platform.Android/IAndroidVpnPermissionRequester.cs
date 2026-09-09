namespace DiTunnel.Platform.Android;

public interface IAndroidVpnPermissionRequester
{
    Task<bool> RequestAsync(CancellationToken cancellationToken = default);
}
