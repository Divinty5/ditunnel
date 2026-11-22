using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace DiTunnel.App.Controls;

internal sealed class AdaptiveTilePanel(bool mobile) : VirtualizingPanel, ILogicalScrollable
{
    internal const double TileHeight = 80;
    internal const double MaximumViewportHeight = 420;
    private const double Spacing = 6;
    private const double RowStride = TileHeight + Spacing;
    private const double MaximumTileWidth = 360;

    private readonly SortedDictionary<int, Tile> realized = [];
    private readonly Dictionary<object, Stack<Control>> recycled = [];
    private readonly List<int> expired = [];
    private Size extent;
    private Size viewport;
    private int columns = 1;
    private double tileWidth;
    private double verticalOffset;
    private (int Index, double OffsetInRow)? resizeAnchor;
    private bool measuring;
    private bool notifyingScroll;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public bool IsLogicalScrollEnabled => true;
    public Size Extent => extent;
    public Size Viewport => viewport;
    public Size ScrollSize => new(0, 50);
    public Size PageScrollSize => viewport;
    public Vector Offset
    {
        get => new(0, verticalOffset);
        set => SetOffset(value.Y);
    }

    public event EventHandler? ScrollInvalidated;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ItemContainerGenerator is null) return default;
        measuring = true;
        try
        {
            var count = Items.Count;
            var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MaximumTileWidth;
            var minimumWidth = mobile ? 220 : 200;
            var columnsThatFit = Math.Max(1, (int)Math.Floor((width + Spacing) / (minimumWidth + Spacing)));
            var newColumns = Math.Min(columnsThatFit, Math.Max(1, count));
            var height = count == 0 ? 0 : ((count - 1) / newColumns + 1) * RowStride - Spacing;
            var viewportHeight = Math.Min(availableSize.Height, MaximumViewportHeight);
            var newExtent = new Size(width, height);
            var newViewport = new Size(width, viewportHeight);

            // Keep the same card in view when the viewport changes between one and several columns.
            var newOffset = verticalOffset;
            if (columns != newColumns && (verticalOffset > 0 || resizeAnchor is not null))
            {
                var oldRow = (int)(verticalOffset / RowStride);
                if (verticalOffset - oldRow * RowStride >= TileHeight) oldRow++;
                var anchor = resizeAnchor ??= (oldRow * columns, verticalOffset - oldRow * RowStride);
                newOffset = anchor.Index / newColumns * RowStride + anchor.OffsetInRow;
            }
            newOffset = Math.Clamp(newOffset, 0, Math.Max(0, height - viewportHeight));
            columns = newColumns;
            tileWidth = (width - (columns - 1) * Spacing) / columns;
            if (!mobile || columnsThatFit > 1) tileWidth = Math.Min(MaximumTileWidth, tileWidth);

            if (extent != newExtent || viewport != newViewport || verticalOffset != newOffset)
            {
                extent = newExtent;
                viewport = newViewport;
                verticalOffset = newOffset;
                RaiseScrollInvalidated(EventArgs.Empty);
            }

            // Retain one extra row on either side to reuse during small scrolls.
            var first = Math.Max(0, (int)(verticalOffset / RowStride) - 1) * columns;
            var end = Math.Min(count, ((int)Math.Ceiling((verticalOffset + viewportHeight) / RowStride) + 1) * columns);
            foreach (var (index, tile) in realized)
            {
                var stillAtIndex = index < count && ReferenceEquals(tile.Item, Items[index]);
                if (!stillAtIndex || ((index < first || index >= end) && !tile.Control.IsKeyboardFocusWithin))
                    expired.Add(index);
            }
            foreach (var index in expired) Recycle(index);
            expired.Clear();
            for (var index = first; index < end; index++) GetOrCreate(index);
            foreach (var tile in realized.Values) tile.Control.Measure(new Size(tileWidth, TileHeight));
            return new Size(width, Math.Min(height, viewportHeight));
        }
        finally { measuring = false; }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var usedWidth = columns * tileWidth + (columns - 1) * Spacing;
        var startX = mobile ? Math.Max(0, (finalSize.Width - usedWidth) / 2) : 0;
        foreach (var (index, tile) in realized)
        {
            var x = startX + index % columns * (tileWidth + Spacing);
            var y = index / columns * RowStride - verticalOffset;
            tile.Control.Arrange(new Rect(x, y, tileWidth, TileHeight));
        }
        return finalSize;
    }

    protected override Control? ContainerFromIndex(int index) =>
        index >= 0 && index < Items.Count && realized.TryGetValue(index, out var tile) && ReferenceEquals(tile.Item, Items[index])
            ? tile.Control : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, tile) in realized)
            if (ReferenceEquals(tile.Control, container) && ContainerFromIndex(index) is not null) return index;
        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers()
    {
        foreach (var index in realized.Keys)
            if (ContainerFromIndex(index) is { } container) yield return container;
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (measuring || index < 0 || index >= Items.Count || !IsEffectivelyVisible) return null;
        if (!IsMeasureValid) UpdateLayout();
        if (viewport.Height <= 0) return null;
        var top = index / columns * RowStride;
        Reveal(top, top + TileHeight);
        if (ContainerFromIndex(index) is { } existing) return existing;
        // Keyboard navigation needs the target container before the scheduled layout pass.
        if (realized.ContainsKey(index)) Recycle(index);
        return GetOrCreate(index);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        if (Items.Count == 0) return null;
        if (direction == NavigationDirection.First) return ScrollIntoView(0);
        if (direction == NavigationDirection.Last) return ScrollIntoView(Items.Count - 1);
        if (from is not Control control) return null;
        var index = IndexFromContainer(control);
        if (index < 0) return null;
        var target = direction switch
        {
            NavigationDirection.Left when columns > 1 => index - 1,
            NavigationDirection.Right when columns > 1 => index + 1,
            NavigationDirection.Previous => index - 1,
            NavigationDirection.Next => index + 1,
            NavigationDirection.Up => index - columns,
            NavigationDirection.Down when index / columns < (Items.Count - 1) / columns => Math.Min(index + columns, Items.Count - 1),
            NavigationDirection.Down => Items.Count,
            _ => index
        };
        if (target == index) return from;
        if (wrap && (target < 0 || target >= Items.Count)) target = target < 0 ? Items.Count - 1 : 0;
        return ScrollIntoView(target);
    }

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => GetControl(direction, from, wrap: false) as Control;

    public bool BringIntoView(Control target, Rect targetRect)
    {
        if (measuring) return false;
        var container = target as ListBoxItem ?? target.FindAncestorOfType<ListBoxItem>();
        if (container is null || target.TranslatePoint(targetRect.Position, container) is not { } position) return false;
        var index = IndexFromContainer(container);
        if (index < 0) return false;
        // A focus request can arrive before the arrange pass for the new offset.
        var top = index / columns * RowStride + position.Y;
        return Reveal(top, top + targetRect.Height);
    }

    public void RaiseScrollInvalidated(EventArgs e)
    {
        notifyingScroll = true;
        try { ScrollInvalidated?.Invoke(this, e); }
        finally { notifyingScroll = false; }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        resizeAnchor = null;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            while (realized.Count > 0) Recycle(realized.First().Key);
            recycled.Clear();
        }
        InvalidateMeasure();
    }

    private bool Reveal(double top, double bottom)
    {
        if (top < verticalOffset) return SetOffset(top);
        if (bottom > verticalOffset + viewport.Height) return SetOffset(bottom - viewport.Height);
        return false;
    }

    private bool SetOffset(double offset)
    {
        if (notifyingScroll) return false;
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height));
        if (offset == verticalOffset) return false;
        verticalOffset = offset;
        resizeAnchor = null;
        InvalidateMeasure();
        RaiseScrollInvalidated(EventArgs.Empty);
        return true;
    }

    private Control GetOrCreate(int index)
    {
        if (realized.TryGetValue(index, out var tile)) return tile.Control;
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        var needsContainer = generator.NeedsContainer(item, index, out var key);
        var container = !needsContainer ? (Control)item! : CreateOrReuse();
        realized.Add(index, new Tile(item, container, needsContainer ? key : null));
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;

        Control CreateOrReuse()
        {
            if (key is not null && recycled.TryGetValue(key, out var pool) && pool.TryPop(out var reused)) return reused;
            return generator.CreateContainer(item, index, key);
        }
    }

    private void Recycle(int index)
    {
        var tile = realized[index];
        realized.Remove(index);
        ItemContainerGenerator!.ClearItemContainer(tile.Control);
        RemoveInternalChild(tile.Control);
        if (tile.Key is not { } key) return;
        if (!recycled.TryGetValue(key, out var pool)) recycled.Add(key, pool = new Stack<Control>());
        pool.Push(tile.Control);
    }

    private readonly record struct Tile(object? Item, Control Control, object? Key);
}
