using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware;
using Android.OS;
using Android.Views;
using Android.Widget;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using Camera = Android.Hardware.Camera;

namespace DiTunnel.Android;

[Activity(
    Name = "com.divintyinteractive.ditunnel.QrScannerActivity",
    Label = "Сканирование QR-кода",
    Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false)]
public sealed class QrScannerActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    public const int RequestCode = 47312;
    public const string ResultExtra = "qr_result";
    private const int CameraPermissionRequest = 47313;
    private SurfaceView? preview;
    private Camera? camera;
    private int decoding;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new FrameLayout(this) { Background = new ColorDrawable(Color.Black) };
        preview = new SurfaceView(this);
        root.AddView(preview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        var hint = new TextView(this)
        {
            Text = "Наведите камеру на QR-код",
            TextSize = 18,
            Gravity = GravityFlags.Center
        };
        hint.SetTextColor(Color.White);
        hint.SetBackgroundColor(Color.Argb(150, 0, 0, 0));
        root.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 150, GravityFlags.Bottom)
        {
            BottomMargin = 120
        });
        SetContentView(root);
        preview.Holder?.AddCallback(this);
        if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
            RequestPermissions([Manifest.Permission.Camera], CameraPermissionRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == CameraPermissionRequest && grantResults.FirstOrDefault() != Permission.Granted)
            Finish();
        else if (requestCode == CameraPermissionRequest && preview?.Holder?.Surface?.IsValid == true)
            StartCamera(preview.Holder);
    }

    public void SurfaceCreated(ISurfaceHolder holder) => StartCamera(holder);
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) => StopCamera();

    private void StartCamera(ISurfaceHolder holder)
    {
        if (camera is not null || CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted) return;
        try
        {
            camera = Camera.Open();
            camera?.SetDisplayOrientation(90);
            if (camera?.GetParameters() is { } parameters)
            {
                if (parameters.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true)
                    parameters.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
                camera.SetParameters(parameters);
            }
            camera?.SetPreviewDisplay(holder);
            camera?.SetPreviewCallback(this);
            camera?.StartPreview();
        }
        catch { StopCamera(); Finish(); }
    }

    public void OnPreviewFrame(byte[]? data, Camera? source)
    {
        if (data is null || source?.GetParameters()?.PreviewSize is not { } size || Interlocked.Exchange(ref decoding, 1) != 0) return;
        var frame = data.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                var result = Decode(frame, size.Width, size.Height);
                if (!string.IsNullOrWhiteSpace(result?.Text)) RunOnUiThread(() => Complete(result.Text));
            }
            catch (ReaderException) { }
            finally { Interlocked.Exchange(ref decoding, 0); }
        });
    }

    private static ZXing.Result? Decode(byte[] frame, int width, int height)
    {
        var result = DecodeFrame(frame, width, height);
        if (result is not null) return result;

        // Camera preview buffers keep the sensor's landscape orientation even while the
        // activity is portrait. Try the rotated luminance plane as well so the user does
        // not have to turn the phone to make a QR code recognizable.
        var rotated = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            rotated[x * height + height - y - 1] = frame[y * width + x];
        return DecodeFrame(rotated, height, width);
    }

    private static ZXing.Result? DecodeFrame(byte[] luminanceBytes, int width, int height)
    {
        try
        {
            var luminance = new PlanarYUVLuminanceSource(luminanceBytes, width, height, 0, 0, width, height, false);
            var bitmap = new BinaryBitmap(new HybridBinarizer(luminance));
            return new QRCodeReader().decode(bitmap, new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true });
        }
        catch (ReaderException) { return null; }
    }

    private void Complete(string text)
    {
        StopCamera();
        SetResult(global::Android.App.Result.Ok, new Intent().PutExtra(ResultExtra, text));
        Finish();
    }

    private void StopCamera()
    {
        var current = camera;
        camera = null;
        if (current is null) return;
        try { current.SetPreviewCallback(null); current.StopPreview(); current.Release(); }
        catch { }
        current.Dispose();
    }

    protected override void OnDestroy() { StopCamera(); base.OnDestroy(); }
}
