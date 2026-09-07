using Avalonia;
using Avalonia.Controls;

namespace DiTunnel.App.Controls;

public sealed class AdaptiveTilePanel : Panel
{
    public double MinimumTileWidth { get; set; } = 220;
    public double MaximumTileWidth { get; set; } = 420;
    public double TileSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var metrics = CalculateMetrics(availableSize.Width, Children.Count);
        var height = 0d;
        for (var rowStart = 0; rowStart < Children.Count; rowStart += metrics.Columns)
        {
            var rowHeight = 0d;
            var rowEnd = Math.Min(rowStart + metrics.Columns, Children.Count);
            for (var index = rowStart; index < rowEnd; index++)
            {
                Children[index].Measure(new Size(metrics.TileWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
            }
            height += rowHeight;
        }
        var rows = (int)Math.Ceiling(Children.Count / (double)metrics.Columns);
        return new Size(metrics.UsedWidth, Math.Max(0, height + Math.Max(0, rows - 1) * TileSpacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var metrics = CalculateMetrics(finalSize.Width, Children.Count);
        var y = 0d;
        for (var rowStart = 0; rowStart < Children.Count; rowStart += metrics.Columns)
        {
            var rowHeight = 0d;
            var rowEnd = Math.Min(rowStart + metrics.Columns, Children.Count);
            for (var index = rowStart; index < rowEnd; index++) rowHeight = Math.Max(rowHeight, Children[index].DesiredSize.Height);
            for (var index = rowStart; index < rowEnd; index++)
            {
                var column = index - rowStart;
                Children[index].Arrange(new Rect(column * (metrics.TileWidth + TileSpacing), y, metrics.TileWidth, rowHeight));
            }
            y += rowHeight + TileSpacing;
        }
        return finalSize;
    }

    private (int Columns, double TileWidth, double UsedWidth) CalculateMetrics(double availableWidth, int count)
    {
        var width = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : MaximumTileWidth;
        var columns = Math.Max(1, (int)Math.Floor((width + TileSpacing) / (MinimumTileWidth + TileSpacing)));
        columns = Math.Min(columns, Math.Max(1, count));
        var tileWidth = Math.Min(MaximumTileWidth, Math.Max(0, (width - (columns - 1) * TileSpacing) / columns));
        return (columns, tileWidth, columns * tileWidth + (columns - 1) * TileSpacing);
    }
}
