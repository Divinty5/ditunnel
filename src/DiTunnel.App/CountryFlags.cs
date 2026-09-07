using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DiTunnel.App;

public static class CountryFlags
{
    private static readonly Dictionary<string, Bitmap?> Images = new(StringComparer.OrdinalIgnoreCase);
    public static Bitmap? Get(string? code)
    {
        if (code is not { Length: 2 } || !code.All(char.IsAsciiLetter)) return null;
        if (Images.TryGetValue(code, out var found)) return found;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://DiTunnel.App/Assets/Flags/{code.ToLowerInvariant()}.png"));
            return Images[code] = new Bitmap(stream);
        }
        catch { return Images[code] = null; }
    }
}
