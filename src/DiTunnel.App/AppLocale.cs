namespace DiTunnel.App;

public static class AppLocale
{
    public static Action<string>? ApplyPlatformLocale { get; set; }

    public static void Apply() => ApplyPlatformLocale?.Invoke(UserSettings.Current.Language);
}
