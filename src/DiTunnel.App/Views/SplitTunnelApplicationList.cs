using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Connection;

namespace DiTunnel.App.Views;

internal sealed class SplitTunnelApplicationList : UserControl
{
    private readonly ListBox list = new()
    {
        Background = Brushes.Transparent,
        IsVisible = false,
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
        ItemTemplate = new FuncDataTemplate<ApplicationRow>((_, _) =>
        {
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.Bind(TextBlock.TextProperty, CompiledBinding.Create<ApplicationRow, string>(row => row.Label));
            var check = new CheckBox
            {
                Content = label,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6)
            };
            check.Bind(ToggleButton.IsCheckedProperty,
                CompiledBinding.Create<ApplicationRow, bool>(row => row.IsSelected, mode: BindingMode.TwoWay));
            return check;
        }, supportsRecycling: true)
    };
    private readonly TextBlock loading = new() { Text = L.T("Загружаем список приложений…"), TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<string> selected;
    private readonly ReleaseTarget platform;
    private ApplicationRow[] rows = [];
    private string query = "";

    public SplitTunnelApplicationList(HashSet<string> selected, ReleaseTarget platform)
    {
        this.selected = selected;
        this.platform = platform;
        Height = 260;
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        ScrollViewer.SetVerticalScrollBarVisibility(list, OperatingSystem.IsAndroid() ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetIsScrollChainingEnabled(list, true);
        Content = new Grid { Children = { list, loading } };
    }

    public void SetApplications(IEnumerable<InstalledApplication> applications)
    {
        rows = applications.OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(application => new ApplicationRow(application, platform, selected)).ToArray();
        loading.IsVisible = false;
        list.IsVisible = true;
        ApplyFilter();
    }

    public void Filter(string? text)
    {
        query = text?.Trim() ?? "";
        ApplyFilter();
    }

    public void RefreshSelection()
    {
        foreach (var row in rows) row.RefreshSelection();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var matches = rows.Where(row => row.Matches(query)).ToArray();
        list.ItemsSource = matches.Where(row => row.IsSelected).Concat(matches.Where(row => !row.IsSelected)).ToArray();
    }

    private sealed class ApplicationRow(InstalledApplication application, ReleaseTarget platform, HashSet<string> selected) : ObservableObject
    {
        private readonly string displayId = ApplicationSelectionPresets.DisplayId(application.Id, platform);
        public string Label => field ??= $"{application.Name}\n{displayId}";
        private bool isSelected = selected.Contains(application.Id);
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (value) selected.Add(application.Id);
                else selected.Remove(application.Id);
                SetProperty(ref isSelected, value);
            }
        }

        public bool Matches(string query) => query.Length == 0 ||
            application.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            application.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            displayId.Contains(query, StringComparison.OrdinalIgnoreCase);

        public void RefreshSelection() => SetProperty(ref isSelected, selected.Contains(application.Id), nameof(IsSelected));
    }
}
