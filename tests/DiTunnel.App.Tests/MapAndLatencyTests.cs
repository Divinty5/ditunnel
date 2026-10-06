using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Media;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Tests;

public sealed class MapAndLatencyTests
{
    [Fact]
    public void AllTwentyLightsUseTheMapViewportAndTheirCentresAreOnLand()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src/DiTunnel.App/Assets"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string assets = Path.Combine(directory.FullName, "src/DiTunnel.App/Assets");
        XNamespace ns = "http://www.w3.org/2000/svg";
        var map = XDocument.Load(Path.Combine(assets, "world-map.svg"));
        string viewport = map.Root!.Attribute("viewBox")!.Value;
        Assert.Equal("0 0 1600 760", viewport);
        string data = map.Descendants(ns + "path").Single().Attribute("d")!.Value;
        var rings = Regex.Matches(data, "M([^Z]+)Z").Select(match => Regex.Matches(match.Groups[1].Value, @"(-?[\d.]+),(-?[\d.]+)")
            .Select(point => (X: double.Parse(point.Groups[1].Value, CultureInfo.InvariantCulture),
                Y: double.Parse(point.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray()).ToArray();
        Assert.True(rings.Sum(ring => ring.Length) > 4000);
        for (int i = 1; i <= 20; i++)
        {
            var light = XDocument.Load(Path.Combine(assets, $"world-map-light-{i:00}.svg"));
            Assert.Equal(viewport, light.Root!.Attribute("viewBox")!.Value);
            var coords = Regex.Match(light.Descendants(ns + "g").Single().Attribute("transform")!.Value, @"translate\(([\d.]+) ([\d.]+)\)");
            double x = double.Parse(coords.Groups[1].Value, CultureInfo.InvariantCulture), y = double.Parse(coords.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.True(rings.Count(ring => Contains(x, y, ring)) % 2 == 1, $"Light {i} is outside land.");
        }
    }
    private static bool Contains(double x, double y, (double X, double Y)[] ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y) &&
                x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X) inside = !inside;
        return inside;
    }
    [Theory]
    [InlineData(100d, false)]
    [InlineData(1500d, false)]
    [InlineData(2500d, false)]
    [InlineData(3500d, false)]
    [InlineData(5000d, false)]
    [InlineData(null, false)]
    [InlineData(100d, true)]
    [InlineData(1500d, true)]
    [InlineData(2500d, true)]
    [InlineData(3500d, true)]
    [InlineData(5000d, true)]
    [InlineData(null, true)]
    public void AllLatencyColoursRemainReadableOnTheirThemeBadge(double? delay, bool light)
    {
        var brush = LatencyPalette.For(delay);
        var color = ((ISolidColorBrush)LatencyPalette.ForTheme(brush, light)).Color;
        var background = Color.Parse(light ? "#E7E2FB" : "#221D37");
        double a = Luminance(color), b = Luminance(background);
        Assert.True((Math.Max(a,b) + .05) / (Math.Min(a,b) + .05) >= 4.5);
    }
    [Fact]
    public void ThemeConverterChangesShadesWithoutChangingThresholdCategories()
    {
        var brush = LatencyPalette.For(500);
        var converter = LatencyThemeConverter.Instance;
        var dark = converter.Convert([brush, Avalonia.Styling.ThemeVariant.Dark], typeof(IBrush), null, CultureInfo.InvariantCulture);
        var light = converter.Convert([brush, Avalonia.Styling.ThemeVariant.Light], typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.Same(brush, dark);
        Assert.NotSame(dark, light);
        Assert.Equal(Color.Parse("#167344"), ((ISolidColorBrush)light!).Color);
        Assert.Same(dark, converter.Convert([brush, Avalonia.Styling.ThemeVariant.Dark], typeof(IBrush), null, CultureInfo.InvariantCulture));
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { double v = value / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}
