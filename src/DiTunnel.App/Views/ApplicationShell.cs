using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace DiTunnel.App.Views;

// Navigation changes only the page; notification remains above pages and their overlays.
public sealed class ApplicationShell : UserControl
{
    public ContentControl NavigationHost { get; } = new() { Content = new MainView(),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    public ApplicationShell()
    {
        var root = new Grid();
        root.Children.Add(NavigationHost);
        var text = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new Binding("RefreshToast") { Converter = new TranslationConverter() });
        var toast = new Border { Name = "GlobalRefreshToast", Child = text, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 16, 16, 28), ZIndex = 100 };
        toast.Bind(Visual.IsVisibleProperty, new Binding("IsRefreshToastVisible"));
        toast.Bind(Border.BackgroundProperty, new Binding("RefreshToastBackground"));
        root.Children.Add(toast);
        Content = root;
    }
}
