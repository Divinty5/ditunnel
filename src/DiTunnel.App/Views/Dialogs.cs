using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DiTunnel.App.Views;

public static class Dialogs
{
    public static Button Button(string text, Action action)
    {
        var button = new Button { Content = L.T(text), Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(10) };
        button.Click += (_, _) => action();
        return button;
    }
    internal static Button AsyncButton(string text, Func<Task> action)
    {
        var button = new Button { Content = L.T(text), Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(10) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    internal static TextBlock Label(string text) => new() { Text = L.T(text), TextWrapping = TextWrapping.Wrap };
    private static ScrollViewer Scroll(Control content)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = OperatingSystem.IsAndroid() ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto
        };
        scroll.Bind(ScrollViewer.BackgroundProperty, scroll.GetResourceObservable("PageBrush"));
        return scroll;
    }
    internal static Control Page(string title, Button back, Control content, bool mobileScroll = false)
    {
        var heading = new TextBlock
        {
            Text = L.T(title), FontSize = 24, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0)
        };
        heading.Bind(TextBlock.ForegroundProperty, heading.GetResourceObservable("AccentTextBrush"));
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        headerGrid.Children.Add(back); headerGrid.Children.Add(heading); Grid.SetColumn(heading, 1);
        var header = new Border { Padding = new Thickness(16, 9), Child = headerGrid };
        header.Bind(Border.BackgroundProperty, header.GetResourceObservable("PageBrush"));
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        void UpdateCompactTitle() => heading.Text = title == "Подписки и профили" && root.Bounds.Width < 560
            ? L.T("Подписки") : L.T(title);
        root.SizeChanged += (_, _) => UpdateCompactTitle();
        root.Bind(Grid.BackgroundProperty, root.GetResourceObservable("PageBrush"));
        root.Children.Add(header);
        var scroll = Scroll(content);
        if (mobileScroll)
        {
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        root.Children.Add(scroll); Grid.SetRow(scroll, 1);
        return root;
    }
    private static Window Window(Window owner, string title, double height = 560) => new()
    {
        Title = L.T(title), Width = Math.Min(560, owner.Width), Height = Math.Min(height, owner.Height),
        MinWidth = 320, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Icon = owner.Icon
    };

    public static Task<string?> AskUpdate(ContentControl owner, AppRelease release, bool canInstall)
    {
        if (owner.Content is not Control original) return Task.FromResult<string?>(null);
        var completion = new TaskCompletionSource<string?>();
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = L.T("Доступно обновление"), FontSize = 22, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(Label($"Доступна версия {ReleaseChecker.FormatVersion(release.Version)}. Установленная версия: {UserSettings.Version}."));
        panel.Children.Add(Label(canInstall
            ? "Установщик будет загружен и проверен по SHA-256. Перед его запуском VPN будет отключён."
            : "Нет совместимого установщика или файла SHA-256. Откройте GitHub в настройках, в разделе «Обновления»."));
        var overlay = new Grid();
        void Complete(string? result)
        {
            overlay.Children.Remove(original);
            owner.Content = original;
            completion.TrySetResult(result);
        }
        if (canInstall) panel.Children.Add(Button(App.Platform.ReleaseTarget == ReleaseTarget.AndroidArm64
            ? "Скачать и установить" : "Обновить и перезапустить", () => Complete("install")));
        panel.Children.Add(Button("Позже", () => Complete("later")));
        var card = new Border { Margin = new Thickness(16), MaxWidth = 560, Padding = new Thickness(24), Background = Brushes.Black,
            CornerRadius = new CornerRadius(18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = panel };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CardBrush"));
        owner.Content = null;
        overlay.Children.Add(original);
        overlay.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), Child = card });
        owner.Content = overlay;
        return completion.Task;
    }

    public static async Task<string?> AskClose(Window owner)
    {
        var dialog = Window(owner, "Закрыть Di-Tunnel?", 260);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Label(App.Platform.SupportsHideToTray
            ? "Скрыть приложение и оставить VPN работающим или выйти и отключить VPN?"
            : "Выйти из Di-Tunnel?"));
        if (App.Platform.SupportsHideToTray) panel.Children.Add(Button("Скрыть в трей", () => dialog.Close("hide")));
        panel.Children.Add(Button("Выйти", () => dialog.Close("exit")));
        panel.Children.Add(Button("Отмена", () => dialog.Close((string?)null)));
        dialog.Content = Scroll(panel);
        return await dialog.ShowDialog<string?>(owner);
    }
}
