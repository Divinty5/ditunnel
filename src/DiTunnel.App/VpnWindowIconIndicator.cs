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

    public VpnWindowIconIndicator(Window window)
    {
        this.window = window;
        originalIcon = window.Icon;
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
            if (!ReferenceEquals(window.Icon, icon)) window.Icon = icon;
        }
        catch (Exception error)
        {
            // Cosmetic updates must not interrupt VPN operation.
            Trace.TraceWarning("Unable to update VPN window icon: {0}", error.Message);
        }
    }

    internal static WindowIcon CreateIcon(Color accent)
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
                    var tinted = ConnectionPalette.RecolourTunnelPixel(pixel, accent);
                    row[index] = tinted.B; row[index + 1] = tinted.G; row[index + 2] = tinted.R;
                }
                Marshal.Copy(row, 0, address, row.Length);
            }
        }
        return new WindowIcon(bitmap);
    }

    public void Dispose()
    {
        window.DataContextChanged -= DataContextChanged;
        if (model is not null) model.PropertyChanged -= ModelPropertyChanged;
        model = null;
        icons.Clear();
    }
}
