using Avalonia;
using Avalonia.Controls;
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
    private readonly MainView mainView = new();
    public ContentControl NavigationHost { get; }
    private readonly ContentControl subscriptionsHost = new() { IsVisible = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    public ApplicationShell()
    {
        NavigationHost = new ContentControl { Content = mainView,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
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

    internal void SetMapHostVisible(bool visible) => mainView.SetMapHostVisible(visible);

    internal void OpenSubscriptions(MainViewModel model)
    {
        SubscriptionsPage.Show(subscriptionsHost, model, CloseSubscriptions);
        NavigationHost.IsVisible = false;
        subscriptionsHost.IsVisible = true;
    }

    internal void RestoreSubscriptionsOnResume()
    {
        if (subscriptionsHost.Content is not Control { DataContext: MainViewModel model } page) return;

        // Android can retain layout and input while losing the drawing of controls after resume.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoaded || !ReferenceEquals(subscriptionsHost.Content, page)) return;
            var pageOffset = page.FindDescendantOfType<ScrollViewer>()?.Offset;
            var listOffset = page.FindDescendantOfType<ListBox>()?.FindDescendantOfType<ScrollViewer>()?.Offset;
            OpenSubscriptions(model);
            var restored = (Control)subscriptionsHost.Content!;
            restored.Loaded += RestoreScrollOffsets;

            void RestoreScrollOffsets(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            {
                restored.Loaded -= RestoreScrollOffsets;
                if (listOffset is { } inner && restored.FindDescendantOfType<ListBox>()?.FindDescendantOfType<ScrollViewer>() is { } listScroll)
                    listScroll.Offset = inner;
                // The virtualizing profile list may change the page extent on its first layout.
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(subscriptionsHost.Content, restored) && pageOffset is { } outer &&
                        restored.FindDescendantOfType<ScrollViewer>() is { } pageScroll)
                        pageScroll.Offset = outer;
                }, DispatcherPriority.Background);
            }
        }, DispatcherPriority.Loaded);
    }

    private void CloseSubscriptions()
    {
        subscriptionsHost.IsVisible = false;
        subscriptionsHost.Content = null;
        NavigationHost.IsVisible = true;
        AppBackNavigation.Clear();
    }
}
