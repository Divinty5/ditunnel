using Avalonia;
using Avalonia.Controls;

namespace DiTunnel.App.Controls;

internal sealed class SubscriptionActionSlot(WrapPanel probeActions) : Grid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (probeActions.IsVisible) return size;

        // Keep the same height when cancel replaces the potentially wrapped probe buttons.
        var first = probeActions.Children[0];
        var second = probeActions.Children[1];
        first.Measure(availableSize);
        second.Measure(availableSize);
        var one = first.DesiredSize;
        var two = second.DesiredSize;
        var height = one.Width + probeActions.ItemSpacing + two.Width > availableSize.Width
            ? one.Height + probeActions.LineSpacing + two.Height
            : Math.Max(one.Height, two.Height);
        return new Size(size.Width, Math.Max(size.Height, height));
    }
}
