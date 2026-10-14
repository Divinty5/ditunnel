using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Profiles;
using Avalonia.Media;

namespace DiTunnel.App.ViewModels;

public sealed partial class ServerItemViewModel(ImportedProfile profile) : ObservableObject
{
    public ImportedProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Summary => Profile.Summary;
    public string Protocol => Profile.Kind.Equals("SS", StringComparison.OrdinalIgnoreCase) ? "Shadowsocks" : Profile.ProtocolName;
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
    public string ProbeDisplayText => ProbeText == "Проверяем…" || ProbeMilliseconds is not { } delay
        ? ProbeText : $"{delay:0} мс";
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
