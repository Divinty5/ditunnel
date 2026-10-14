using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;

namespace DiTunnel.App;

internal sealed class VpnWindowIconIndicator : IDisposable
{
    private readonly Window window;
    private readonly WindowIcon? originalIcon;
    private readonly Dictionary<Color, WindowIcon> icons = [];
    private MainViewModel? model;
    private readonly WindowsTaskbarIcon? taskbar;
    private readonly LinuxDockIcon? dock;
    private bool disposed;

    public VpnWindowIconIndicator(Window window, bool enableShellHook = true)
    {
        this.window = window;
        originalIcon = OperatingSystem.IsWindows() ? CreateIcon(null) : window.Icon;
        if (OperatingSystem.IsWindows())
        {
            taskbar = new WindowsTaskbarIcon(window, enableShellHook);
            try { taskbar.Update(originalIcon!); }
            catch (Exception error) { Trace.TraceWarning("Unable to initialize VPN taskbar icon: {0}", error.Message); }
        }
        if (OperatingSystem.IsLinux() && enableShellHook)
        {
            try
            {
                var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(data) || !Path.IsPathFullyQualified(data))
                    data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share");
                dock = new LinuxDockIcon(data, AppPaths.StateDirectory);
            }
            catch (Exception error) { Trace.TraceWarning("Unable to initialize Linux dock icon: {0}", error.Message); }
        }
        window.DataContextChanged += DataContextChanged;
        DataContextChanged(null, EventArgs.Empty);
    }

    private void DataContextChanged(object? sender, EventArgs e)
    {
        if (model is not null) model.PropertyChanged -= ModelPropertyChanged;
        model = window.DataContext as MainViewModel;
        if (model is not null) model.PropertyChanged += ModelPropertyChanged;
        Update();
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MainViewModel.ConnectionState))
            Update();
    }

    private void Update()
    {
        try
        {
            var state = model?.ConnectionState ?? VpnConnectionState.Disconnected;
            var icon = originalIcon;
            if (state is VpnConnectionState.Connected or VpnConnectionState.Error)
            {
                var accent = ConnectionPalette.AccentFor(state);
                if (!icons.TryGetValue(accent, out icon))
                    icons[accent] = icon = CreateIcon(accent);
            }
            // The shell component uses this same full icon for the running group.
            if (!ReferenceEquals(window.Icon, icon)) window.Icon = icon;
            if (icon is not null) dock?.Update(icon);
            if (OperatingSystem.IsWindows() && icon is not null)
            {
                try { taskbar?.Update(icon); }
                catch (Exception error) { Trace.TraceWarning("Unable to update VPN taskbar icon: {0}", error.Message); }
            }
        }
        catch (Exception error)
        {
            // Cosmetic updates must not interrupt VPN operation.
            Trace.TraceWarning("Unable to update VPN window icon: {0}", error.Message);
        }
    }

    internal static WindowIcon CreateIcon(Color? accent)
    {
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        using var source = Bitmap.DecodeToWidth(stream, 256);
        using var bitmap = new WriteableBitmap(source.PixelSize, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            source.CopyPixels(buffer);
            var row = new byte[buffer.RowBytes];
            for (var y = 0; y < buffer.Size.Height; y++)
            {
                var address = IntPtr.Add(buffer.Address, y * buffer.RowBytes);
                Marshal.Copy(address, row, 0, row.Length);
                for (var x = 0; x < buffer.Size.Width; x++)
                {
                    var index = x * 4;
                    var pixel = Color.FromArgb(row[index + 3], row[index + 2], row[index + 1], row[index]);
                    var tinted = accent is { } colour ? ConnectionPalette.RecolourTunnelPixel(pixel, colour) : pixel;
                    row[index] = tinted.B; row[index + 1] = tinted.G; row[index + 2] = tinted.R;
                }
                Marshal.Copy(row, 0, address, row.Length);
            }
        }
        if (!OperatingSystem.IsWindows()) return new WindowIcon(bitmap);
        // Raw PNG icons become a single oversized HICON on Win32. Supply the
        // actual caption/taskbar sizes rather than relying on Shell downscaling.
        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true);
        using var bitmapData = new MemoryStream();
        bitmap.Save(bitmapData, new PngBitmapEncoderOptions());
        bitmapData.Position = 0;
        using var scalable = new Bitmap(bitmapData);
        var images = new[] { 16, 24, 32, 48, 64, 256 }.Select(size =>
        {
            using var scaled = scalable.CreateScaledBitmap(new Avalonia.PixelSize(size, size), BitmapInterpolationMode.HighQuality);
            using var png = new MemoryStream();
            scaled.Save(png, new PngBitmapEncoderOptions());
            return (Size: size, Data: png.ToArray());
        }).ToArray();
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Length);
        var offset = 6 + 16 * images.Length;
        foreach (var image in images)
        {
            writer.Write((byte)(image.Size == 256 ? 0 : image.Size));
            writer.Write((byte)(image.Size == 256 ? 0 : image.Size));
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(image.Data.Length); writer.Write(offset);
            offset += image.Data.Length;
        }
        foreach (var image in images) writer.Write(image.Data);
        ico.Position = 0;
        return new WindowIcon(ico);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (OperatingSystem.IsWindows()) taskbar?.Dispose();
        try { dock?.Dispose(); }
        catch (Exception error) { Trace.TraceWarning("Unable to restore Linux dock icon: {0}", error.Message); }
        window.DataContextChanged -= DataContextChanged;
        if (model is not null) model.PropertyChanged -= ModelPropertyChanged;
        model = null;
        icons.Clear();
    }
}
