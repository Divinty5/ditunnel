using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using Avalonia.Media;

namespace DiTunnel.App.ViewModels;

public sealed partial class ServerItemViewModel(ImportedProfile profile) : ObservableObject
{
    public ImportedProfile Profile { get; } = profile;
    public string Name { get; } = DisplayName(profile);
    public string Summary => Profile.Summary;
    public string Protocol => Profile.Kind.Equals("SS", StringComparison.OrdinalIgnoreCase) ? "Shadowsocks" : Profile.ProtocolName;
    [ObservableProperty] private IBrush? activeConnectionBrush;

    private static string DisplayName(ImportedProfile profile)
    {
        const string awg = AmneziaWgConfiguration.ProfileKind;
        if (!profile.Kind.Equals(awg, StringComparison.OrdinalIgnoreCase)) return profile.Name;

        var name = IpLiteral().Replace(profile.Name, match =>
            IPAddress.TryParse(match.Groups["address"].ValueSpan, out _) ? "…" : match.Value);
        return name == "…" || name.Equals(awg + " · …", StringComparison.OrdinalIgnoreCase) ? awg : name;
    }

    [GeneratedRegex("""
        \[ (?<address>[0-9a-f:.]+) \] (?::[0-9]+)?
        | (?<![0-9a-z:]) (?<address>[0-9a-f]*:(?:[0-9a-f]*:)+(?:[0-9a-f.]*[0-9a-f])?) (?![0-9a-z:])
        | (?<![\d.]) (?<address>[0-9]{1,3}(?:\.[0-9]{1,3}){3}) (?::[0-9]+)? (?!\d|\.\d)
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex IpLiteral();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeDisplayText))]
    [NotifyPropertyChangedFor(nameof(ProbeStatusText))]
    private string probeText = "Не проверен";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    [NotifyPropertyChangedFor(nameof(ProbeDisplayText))]
    private double? probeMilliseconds;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool probeTimedOut;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeDisplayText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool isProbeUnconfirmed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeStatusText))]
    [NotifyPropertyChangedFor(nameof(ProbeDisplayText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool isProbePending;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeStatusText))]
    [NotifyPropertyChangedFor(nameof(ProbeDisplayText))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private ServerProbeResult? activeProbeResult;

    internal bool ConnectionAttemptFailed { get; set; }
    internal bool IsUnavailable => !IsProbePending && (ActiveProbeResult is { } connection
        ? !connection.IsDeferred && connection.Milliseconds is null && (ProbeTimedOut || ConnectionAttemptFailed)
        : ProbeTimedOut);
    private double? DisplayedProbeMilliseconds => ActiveProbeResult is { } active ? active.Milliseconds : ProbeMilliseconds;
    public string ProbeStatusText => IsProbePending ? "Проверяем…" : ActiveProbeResult?.Message ?? ProbeText;
    public string ProbeDisplayText => IsProbePending || DisplayedProbeMilliseconds is not { } delay
        ? ProbeStatusText : $"{delay:0} мс{ProbeMethod}";
    private string ProbeMethod
    {
        get
        {
            var method = ActiveProbeResult?.Diagnostics?.Method ?? ProbeStatusText.Split('·', 2)[0].Trim();
            return method is "TCP" or "TLS" or "HTTP" or "HTTPS" ? $" · {method}" : "";
        }
    }
    public IBrush ProbeBrush => IsProbePending
        ? LatencyPalette.For(null)
        : LatencyPalette.For(DisplayedProbeMilliseconds, ActiveProbeResult is { } active
            ? active.Milliseconds is null && !active.IsDeferred : ProbeTimedOut);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagImage))]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    private string? countryCode;
    public Avalonia.Media.Imaging.Bitmap? FlagImage => CountryFlags.Get(CountryCode);
    public bool HasFlag => FlagImage is not null;
    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
}
