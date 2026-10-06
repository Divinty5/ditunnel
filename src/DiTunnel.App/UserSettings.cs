using System.Text.Json;
using System.Reflection;
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
    public ServerProbeMode ServerProbeMode { get; set; } = ServerProbeMode.Fast;
    public string? SelectedSourceId { get; set; }
    public SplitTunnelMode SplitTunnelMode { get; set; } = SplitTunnelMode.ProxyAll;
    // Legacy 0.4.2 fields retained for one-way settings migration.
    public List<string> SplitTunnelDomains { get; set; } = [];
    public List<string> SplitTunnelProcesses { get; set; } = [];
    public List<string> BypassSplitTunnelDomains { get; set; } = [];
    public List<string> BypassSplitTunnelProcesses { get; set; } = [];
    public List<string> ProxySelectedSplitTunnelDomains { get; set; } = [];
    public List<string> ProxySelectedSplitTunnelProcesses { get; set; } = [];
    // Android uses package names; Windows uses executable paths for Xray process routing.
    public SplitTunnelPolicy GetSplitTunnelPolicy()
    {
        var domains = SplitTunnelMode == SplitTunnelMode.ProxySelected ? ProxySelectedSplitTunnelDomains : BypassSplitTunnelDomains;
        var processes = SplitTunnelMode == SplitTunnelMode.ProxySelected ? ProxySelectedSplitTunnelProcesses : BypassSplitTunnelProcesses;
        return new(SplitTunnelMode, domains.ToArray(), processes.ToArray());
    }

    public (List<string> Domains, List<string> Processes) GetSplitTunnelRules(SplitTunnelMode mode) => mode == SplitTunnelMode.ProxySelected
        ? (ProxySelectedSplitTunnelDomains, ProxySelectedSplitTunnelProcesses)
        : (BypassSplitTunnelDomains, BypassSplitTunnelProcesses);

    public string NetworkSettingsFingerprint()
    {
        static string Canonical(IEnumerable<string> values) => string.Join('\n', values
            .Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
        var protection = string.Join('|', KillSwitchEnabled, AllowLocalNetwork, BlockAdsEnabled, StrictAdBlockingEnabled, (int)SplitTunnelMode);
        var rules = GetSplitTunnelPolicy();
        return SplitTunnelMode == SplitTunnelMode.ProxyAll ? protection
            : string.Join('|', protection, Canonical(rules.Domains), Canonical(rules.Processes));
    }

    public void SetSplitTunnelRules(SplitTunnelMode mode, IEnumerable<string> domains, IEnumerable<string> processes)
    {
        var normalizedDomains = domains.ToList();
        var normalizedProcesses = processes.ToList();
        if (mode == SplitTunnelMode.ProxySelected)
        {
            ProxySelectedSplitTunnelDomains = normalizedDomains;
            ProxySelectedSplitTunnelProcesses = normalizedProcesses;
        }
        else
        {
            BypassSplitTunnelDomains = normalizedDomains;
            BypassSplitTunnelProcesses = normalizedProcesses;
        }
    }
    public bool KillSwitchEnabled { get; set; }
    public bool BlockAdsEnabled { get; set; }
    public bool StrictAdBlockingEnabled { get; set; }
    public bool AllowLocalNetwork { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AutoConnect { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; }
    public bool SplitTunnelDefaultsApplied { get; set; }
    private readonly SemaphoreSlim defaultsGate = new(1, 1);

    public bool ApplySplitTunnelDefaults(IEnumerable<InstalledApplication> applications)
    {
        if (SplitTunnelDefaultsApplied) return false;
        SplitTunnelDefaultsApplied = true;
        if (SplitTunnelMode != SplitTunnelMode.ProxyAll) return false;
        BypassSplitTunnelProcesses = BypassSplitTunnelProcesses
            .Concat(ApplicationSelectionPresets.Select(SplitTunnelMode.BypassSelected, applications))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SplitTunnelMode = SplitTunnelMode.BypassSelected;
        return true;
    }

    public async Task<bool> InitializeSplitTunnelDefaultsAsync(Func<Task<IReadOnlyList<InstalledApplication>>> discover)
    {
        await defaultsGate.WaitAsync();
        try
        {
            if (SplitTunnelDefaultsApplied) return false;
            var applications = await discover();
            bool changed = ApplySplitTunnelDefaults(applications);
            Save();
            return changed;
        }
        finally { defaultsGate.Release(); }
    }
    public string? SkippedUpdateVersion { get; set; }
    public ConnectionPolicy GetConnectionPolicy() => new ConnectionPolicy(KillSwitchEnabled, AllowLocalNetwork, StartWithWindows, AutoConnect).Normalize();
    public WindowPlacement? Window { get; set; }
    public static string Version => typeof(UserSettings).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == (OperatingSystem.IsAndroid() ? "AndroidVersion" : "WindowsVersion"))?.Value
        ?? typeof(UserSettings).Assembly.GetName().Version?.ToString(3) ?? "0.5.13";

    public static UserSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.UserSettings) ?? new();
            if (settings.Language is not ("ru" or "en" or "es" or "zh-Hans")) settings.Language = "ru";
            if (settings.Theme is not ("system" or "dark" or "light")) settings.Theme = "system";
            if (settings.CloseAction is not ("ask" or "hide" or "exit")) settings.CloseAction = "ask";
            if (!Enum.IsDefined(settings.ServerProbeMode)) settings.ServerProbeMode = ServerProbeMode.Fast;
            if (settings.BypassSplitTunnelDomains.Count == 0 && settings.ProxySelectedSplitTunnelDomains.Count == 0
                && settings.BypassSplitTunnelProcesses.Count == 0 && settings.ProxySelectedSplitTunnelProcesses.Count == 0
                && (settings.SplitTunnelDomains.Count > 0 || settings.SplitTunnelProcesses.Count > 0))
                settings.SetSplitTunnelRules(settings.SplitTunnelMode, settings.SplitTunnelDomains, settings.SplitTunnelProcesses);
            return settings;
        }
        catch { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, AppJsonContext.Default.UserSettings));
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
        width = Math.Clamp(width, Math.Min(360, maxWidth), maxWidth);
        height = Math.Clamp(height, Math.Min(500, maxHeight), maxHeight);
        var x = saved?.X ?? area.Right - (int)(width * scaling);
        var y = saved?.Y ?? area.Y;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)(width * scaling)));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - (int)((height + 40) * scaling)));
        return new(x, y, width, height, saved?.Maximized ?? false);
    }
}
