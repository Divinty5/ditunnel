using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Profiles;
using Avalonia.Media;

namespace DiTunnel.App.ViewModels;

public sealed partial class ServerItemViewModel(ImportedProfile profile) : ObservableObject
{
    public ImportedProfile Profile { get; } = profile;
    public string Name { get; } = DisplayName(profile);
    public string Summary => Profile.Summary;
    public string Protocol => Profile.Kind.Equals("SS", StringComparison.OrdinalIgnoreCase) ? "Shadowsocks" : Profile.ProtocolName;

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
    public string ProbeDisplayText => ProbeText == "Проверяем…" || ProbeMilliseconds is not { } delay
        ? ProbeText : $"{delay:0} мс{ProbeMethod}";
    private string ProbeMethod
    {
        get
        {
            var method = ProbeText.Split('·', 2)[0].Trim();
            return method is "TCP" or "TLS" or "HTTP" or "HTTPS" ? $" · {method}" : "";
        }
    }
    public IBrush ProbeBrush => ProbeText == "Проверяем…"
        ? LatencyPalette.For(null)
        : LatencyPalette.For(ProbeMilliseconds, ProbeTimedOut);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagImage))]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    private string? countryCode;
    public Avalonia.Media.Imaging.Bitmap? FlagImage => CountryFlags.Get(CountryCode);
    public bool HasFlag => FlagImage is not null;
    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
}
