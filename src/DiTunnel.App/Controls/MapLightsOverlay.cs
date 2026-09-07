using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Controls;

public sealed class MapLightsOverlay : Control
{
    public static readonly StyledProperty<IEnumerable<MapLightViewModel>?> LightsProperty =
        AvaloniaProperty.Register<MapLightsOverlay, IEnumerable<MapLightViewModel>?>(nameof(Lights));
    public static readonly StyledProperty<Rect> MapBoundsProperty =
        AvaloniaProperty.Register<MapLightsOverlay, Rect>(nameof(MapBounds));

    private IEnumerable<MapLightViewModel>? subscribedLights;

    public IEnumerable<MapLightViewModel>? Lights
    {
        get => GetValue(LightsProperty);
        set => SetValue(LightsProperty, value);
    }
    public Rect MapBounds
    {
        get => GetValue(MapBoundsProperty);
        set => SetValue(MapBoundsProperty, value);
    }

    static MapLightsOverlay() => AffectsRender<MapLightsOverlay>(LightsProperty, MapBoundsProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LightsProperty) return;
        Unsubscribe(subscribedLights);
        subscribedLights = change.GetNewValue<IEnumerable<MapLightViewModel>?>();
        Subscribe(subscribedLights);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var viewport = MapBounds.Width > 0 && MapBounds.Height > 0 ? MapBounds : Bounds;
        if (Lights is null || viewport.Width <= 0 || viewport.Height <= 0) return;

        const double mapWidth = 1600;
        const double mapHeight = 760;
        var scale = Math.Max(viewport.Width / mapWidth, viewport.Height / mapHeight);
        // Bounds from a sibling SVG can be expressed in that SVG's coordinate space.
        // The DrawingContext is local to this overlay, so its origin must remain zero.
        var offsetX = (viewport.Width - mapWidth * scale) / 2;
        var offsetY = (viewport.Height - mapHeight * scale) / 2;

        foreach (var light in Lights)
        {
            if (light.Opacity <= 0.001) continue;
            var center = new Point(offsetX + light.X * scale, offsetY + light.Y * scale);
            var glow = new SolidColorBrush(Color.FromArgb((byte)(light.Opacity * 62), 102, 228, 145));
            var core = new SolidColorBrush(Color.FromArgb((byte)(light.Opacity * 255), 122, 241, 166));
            context.DrawEllipse(glow, null, center, 8 * scale, 8 * scale);
            context.DrawEllipse(core, null, center, 2 * scale, 2 * scale);
        }
    }

    private void Subscribe(IEnumerable<MapLightViewModel>? lights)
    {
        if (lights is INotifyCollectionChanged collection) collection.CollectionChanged += LightsCollectionChanged;
        if (lights is null) return;
        foreach (var light in lights) light.PropertyChanged += LightPropertyChanged;
    }

    private void Unsubscribe(IEnumerable<MapLightViewModel>? lights)
    {
        if (lights is INotifyCollectionChanged collection) collection.CollectionChanged -= LightsCollectionChanged;
        if (lights is null) return;
        foreach (var light in lights) light.PropertyChanged -= LightPropertyChanged;
    }

    private void LightsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    private void LightPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
