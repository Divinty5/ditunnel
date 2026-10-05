namespace DiTunnel.Platform.Android;

// The application supplies its shared catalog without introducing a platform -> UI dependency.
// Read the saved language for isolated services, which can outlive an Activity locale change.
public static class AndroidLocalization
{
    public static Func<string, string> Translate { get; set; } = static text => text;
    public static string T(string text) => Translate(text);
}
