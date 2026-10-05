using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace DiTunnel.Android;

[Activity(
    Label = "Di-Tunnel",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode)]
[IntentFilter(["android.service.quicksettings.action.QS_TILE_PREFERENCES"], Categories = [Intent.CategoryDefault])]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        VpnPermissionCoordinator.Instance.Attach(this);
        QrScannerCoordinator.Instance.Attach(this);
        App.App.ReadClipboardTextAsync = ReadClipboardTextAsync;
        HandleControlIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleControlIntent(intent);
    }
    private void HandleControlIntent(Intent? intent)
    {
        if (intent?.Action != VpnQuickControls.ToggleAction) return;
        // Consume one user action; rotation or a restored Activity must not toggle again.
        intent.SetAction(null);
        _ = VpnQuickControls.ExecuteAsync(VpnQuickControls.ToggleAction);
    }

    protected override void OnDestroy()
    {
        if (App.App.ReadClipboardTextAsync == ReadClipboardTextAsync) App.App.ReadClipboardTextAsync = null;
        VpnPermissionCoordinator.Instance.Detach(this);
        QrScannerCoordinator.Instance.Detach(this);
        base.OnDestroy();
    }

    private Task<string?> ReadClipboardTextAsync()
    {
        // Read only on an explicit paste from the foreground Activity. Android clips can
        // contain a URI rather than text; Avalonia's text MIME lookup misses those clips.
        var manager = GetSystemService(ClipboardService) as ClipboardManager;
        var clip = manager?.PrimaryClip;
        if (clip is null) return Task.FromResult<string?>(null);
        for (var index = 0; index < clip.ItemCount; index++)
        {
            using var item = clip.GetItemAt(index);
            var text = item?.CoerceToText(this);
            if (!string.IsNullOrWhiteSpace(text)) return Task.FromResult<string?>(text);
        }
        return Task.FromResult<string?>(null);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!VpnPermissionCoordinator.Instance.TryHandleResult(requestCode, resultCode)
            && !QrScannerCoordinator.Instance.TryHandleResult(requestCode, resultCode, data))
            base.OnActivityResult(requestCode, resultCode, data);
    }

#pragma warning disable CS0672, CA1422
    public override void OnBackPressed()
    {
        if (!App.AppBackNavigation.TryGoBack()) base.OnBackPressed();
    }
#pragma warning restore CS0672, CA1422
}
