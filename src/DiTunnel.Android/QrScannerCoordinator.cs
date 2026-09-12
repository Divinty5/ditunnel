using Android.App;
using Android.Content;

namespace DiTunnel.Android;

internal sealed class QrScannerCoordinator
{
    public static QrScannerCoordinator Instance { get; } = new();

    private readonly object sync = new();
    private MainActivity? activity;
    private TaskCompletionSource<string?>? pending;

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

    public Task<string?> ScanAsync()
    {
        lock (sync)
        {
            if (activity is null)
                return Task.FromException<string?>(new InvalidOperationException("Окно Android недоступно."));
            if (pending is not null) return pending.Task;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            activity.StartActivityForResult(new Intent(activity, typeof(QrScannerActivity)), QrScannerActivity.RequestCode);
            return pending.Task;
        }
    }

    public bool TryHandleResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != QrScannerActivity.RequestCode) return false;
        TaskCompletionSource<string?>? completion;
        lock (sync)
        {
            completion = pending;
            pending = null;
        }
        completion?.TrySetResult(resultCode == Result.Ok ? data?.GetStringExtra(QrScannerActivity.ResultExtra) : null);
        return true;
    }
}
