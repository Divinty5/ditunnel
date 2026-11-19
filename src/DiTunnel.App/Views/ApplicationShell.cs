using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Views;

// Navigation changes only the page; notification remains above pages and their overlays.
public sealed class ApplicationShell : UserControl
{
    public ContentControl NavigationHost { get; } = new() { Content = new MainView(),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ContentControl subscriptionsHost = new() { IsVisible = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private string? subscriptionsLanguage;

    public ApplicationShell()
    {
        var root = new Grid();
        root.Children.Add(NavigationHost);
        root.Children.Add(subscriptionsHost);
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

    internal async Task OpenSubscriptionsAsync(MainViewModel model)
    {
        NavigationHost.IsVisible = false;
        subscriptionsHost.IsVisible = true;
        var language = UserSettings.Current.Language;
        if (subscriptionsHost.Content is not Control page || page.DataContext != model || subscriptionsLanguage != language)
        {
            await Dialogs.Subscriptions(subscriptionsHost, model, CloseSubscriptions);
            subscriptionsLanguage = language;
        }
        else AppBackNavigation.Set(CloseSubscriptions);

        var body = (Control)subscriptionsHost.Content!;
        Dispatcher.UIThread.Post(() =>
        {
            if (!subscriptionsHost.IsVisible || !ReferenceEquals(subscriptionsHost.Content, body)) return;
            body.FindDescendantOfType<ScrollViewer>()?.ScrollToHome();
            // A valid layout can still have stale drawing commands after leaving the page.
            foreach (var visual in body.GetVisualDescendants().Where(visual => visual.IsEffectivelyVisible))
                visual.InvalidateVisual();
        }, DispatcherPriority.Loaded);
    }

    private void CloseSubscriptions()
    {
        subscriptionsHost.IsVisible = false;
        NavigationHost.IsVisible = true;
        AppBackNavigation.Clear();
    }
}
