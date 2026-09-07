using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using DiTunnel.Core.Connection;

namespace DiTunnel.App;

public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

public sealed class UserSettings
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiTunnel");
    public static UserSettings Current { get; } = Load(Path.Combine(DataDirectory, "settings.json"));
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "system";
    public string CloseAction { get; set; } = "ask";
    public bool LowestMode { get; set; }
    public SplitTunnelMode SplitTunnelMode { get; set; } = SplitTunnelMode.ProxyAll;
    public List<string> SplitTunnelDomains { get; set; } = [];
    public List<string> SplitTunnelProcesses { get; set; } = [];
    public SplitTunnelPolicy GetSplitTunnelPolicy() => new(SplitTunnelMode, SplitTunnelDomains, SplitTunnelProcesses);
    public WindowPlacement? Window { get; set; }
    public static string Version => typeof(UserSettings).Assembly.GetName().Version?.ToString(3) ?? "0.3.5";

    public static UserSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new();
            if (settings.Language is not ("ru" or "en")) settings.Language = "ru";
            if (settings.Theme is not ("system" or "dark" or "light")) settings.Theme = "system";
            if (settings.CloseAction is not ("ask" or "hide" or "exit")) settings.CloseAction = "ask";
            return settings;
        }
        catch { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, true);
    }

    public void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = Theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => app.PlatformSettings is null ? ThemeVariant.Dark : ThemeVariant.Default
        };
    }
}

public static class WindowLayout
{
    // Coordinates are physical pixels; sizes are Avalonia DIPs. Allow room for the caption.
    public static WindowPlacement Fit(WindowPlacement? saved, PixelRect area, double scaling)
    {
        scaling = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        var maxWidth = Math.Max(1, area.Width / scaling);
        var maxHeight = Math.Max(1, area.Height / scaling - 40);
        var width = saved?.Width ?? maxWidth / 2;
        var height = saved?.Height ?? maxHeight;
        if (!double.IsFinite(width)) width = maxWidth / 2;
        if (!double.IsFinite(height)) height = maxHeight;
        width = Math.Clamp(width, Math.Min(400, maxWidth), maxWidth);
        height = Math.Clamp(height, Math.Min(520, maxHeight), maxHeight);
        var x = saved?.X ?? area.Right - (int)(width * scaling);
        var y = saved?.Y ?? area.Y;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)(width * scaling)));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - (int)((height + 40) * scaling)));
        return new(x, y, width, height, saved?.Maximized ?? false);
    }
}
