using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            ContentGrid.MinHeight = 0;
            ContentGrid.MaxWidth = Bounds.Width >= 1260 ? 960 : 840;
            BackgroundMap.IsVisible = true;
        };
    }

    private async void SettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await Dialogs.Settings(TopLevel.GetTopLevel(this) is Window window ? window : this, vm);
    }
    private async void SubscriptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await Dialogs.Subscriptions(TopLevel.GetTopLevel(this) is Window window ? window : this, vm);
    }
    private void AddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.OpenImportCommand.Execute(null);
    }

    private async void PasteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) vm.ImportMessage = "В буфере обмена нет текста.";
            else { vm.ImportText = text; vm.ImportMessage = "Проверьте содержимое и нажмите «Импортировать»."; }
        }
        catch { vm.ImportMessage = "Буфер обмена недоступен. Вставьте текст вручную."; }
    }

    private async void CopyErrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Button button) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(vm.Notice);
            button.Content = "✓";
            await Task.Delay(1500);
            button.Content = vm.CopyErrorText;
        }
        catch { button.Content = "!"; }
    }
}
