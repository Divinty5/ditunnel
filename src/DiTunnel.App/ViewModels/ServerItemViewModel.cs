using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

public sealed partial class ServerItemViewModel(ImportedProfile profile) : ObservableObject
{
    public ImportedProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Summary => Profile.Summary;
    public string Protocol => Profile.Kind;
    [ObservableProperty] private string probeText = "Не проверен";
    [ObservableProperty] private double? probeMilliseconds;
    [ObservableProperty] private bool probeTimedOut;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagImage))]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    private string? countryCode;
    public Avalonia.Media.Imaging.Bitmap? FlagImage => CountryFlags.Get(CountryCode);
    public bool HasFlag => FlagImage is not null;
    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
}
