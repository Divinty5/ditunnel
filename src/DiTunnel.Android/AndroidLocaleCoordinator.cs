using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.OS;
using Java.Util;

namespace DiTunnel.Android;

internal static class AndroidLocaleCoordinator
{
    private static Context? context;

    public static void Initialize(Context applicationContext)
    {
        context = applicationContext;
        App.AppLocale.ApplyPlatformLocale = Apply;
        App.L.Changed += () => ApplyResources(App.UserSettings.Current.Language);
    }

    private static void Apply(string language)
    {
        if (context is null) return;
        var tag = language is "en" or "es" or "zh-Hans" ? language : "ru";
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var manager = context.GetSystemService(Context.LocaleService) as LocaleManager;
            manager?.ApplicationLocales = LocaleList.ForLanguageTags(tag);
            return;
        }

        ApplyResources(tag);
    }

    private static void ApplyResources(string tag)
    {
        if (context is null) return;
#pragma warning disable CA1422
        var locale = Locale.ForLanguageTag(tag);
        Locale.Default = locale;
        var configuration = new Configuration(context.Resources?.Configuration);
        configuration.SetLocale(locale);
        context.Resources?.UpdateConfiguration(configuration, context.Resources.DisplayMetrics);
#pragma warning restore CA1422
    }
}
