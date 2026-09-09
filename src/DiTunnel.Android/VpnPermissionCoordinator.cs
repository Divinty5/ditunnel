using Android.App;
using Android.Content;

namespace DiTunnel.Android;

internal sealed class VpnPermissionCoordinator : DiTunnel.Platform.Android.IAndroidVpnPermissionRequester
{
    private const int RequestCode = 1107;
    private readonly object sync = new();
    private MainActivity? activity;
    private TaskCompletionSource<bool>? pendingRequest;

    public static VpnPermissionCoordinator Instance { get; } = new();

    public void Attach(MainActivity owner)
    {
        lock (sync) activity = owner;
    }

    public void Detach(MainActivity owner)
    {
        lock (sync)
        {
            if (ReferenceEquals(activity, owner)) activity = null;
        }
    }

    public async Task<bool> RequestAsync(CancellationToken cancellationToken = default)
    {
        MainActivity owner;
        TaskCompletionSource<bool> request;
        bool shouldLaunchRequest;
        lock (sync)
        {
            owner = activity ?? throw new InvalidOperationException("Android Activity сейчас недоступна.");
            shouldLaunchRequest = pendingRequest is null;
            request = pendingRequest ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (shouldLaunchRequest) owner.RunOnUiThread(() =>
        {
            var intent = global::Android.Net.VpnService.Prepare(owner);
            if (intent is null) Complete(true);
            else owner.StartActivityForResult(intent, RequestCode);
        });
        return await request.Task.WaitAsync(cancellationToken);
    }

    public bool TryHandleResult(int requestCode, Result resultCode)
    {
        if (requestCode != RequestCode) return false;
        Complete(resultCode == Result.Ok);
        return true;
    }

    private void Complete(bool granted)
    {
        TaskCompletionSource<bool>? request;
        lock (sync)
        {
            request = pendingRequest;
            pendingRequest = null;
        }
        request?.TrySetResult(granted);
    }
}
