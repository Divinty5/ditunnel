using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DiTunnel.Core.Profiles;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Views;

public partial class MainView : UserControl
{
    private bool updateChecked;
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            ContentGrid.MinHeight = 0;
            ContentGrid.MaxWidth = Bounds.Width >= 1260 ? 960 : 840;
            BackgroundMap.IsVisible = true;
        };
        AttachedToVisualTree += async (_, _) =>
        {
            if (updateChecked || !OperatingSystem.IsAndroid() || !UserSettings.Current.CheckForUpdatesAutomatically || DataContext is not MainViewModel vm) return;
            updateChecked = true;
            await UpdateFlow.CheckAsync(this, vm, false);
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
        if (DataContext is not MainViewModel vm) return;
        vm.OpenImportCommand.Execute(null);
        AppBackNavigation.Set(() => CloseImport(vm));
    }

    private async void PasteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = App.ReadClipboardTextAsync is { } readNative
                ? await readNative()
                : clipboard is null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) vm.ImportMessage = "В буфере обмена нет текста.";
            else
            {
                vm.ImportText = text;
                vm.ImportName = "";
                await ImportAndUpdateNavigationAsync(vm);
            }
        }
        catch { vm.ImportMessage = "Буфер обмена недоступен. Вставьте текст вручную."; }
    }

    private async void ScanQrClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var text = await QrScanner.ScanAsync();
            if (string.IsNullOrWhiteSpace(text)) vm.ImportMessage = "Сканирование QR-кода отменено.";
            else
            {
                vm.ImportText = text;
                vm.ImportName = "";
                await ImportAndUpdateNavigationAsync(vm);
            }
        }
        catch (Exception exception)
        {
            vm.ImportMessage = $"Не удалось отсканировать QR-код: {exception.Message}";
        }
    }

    private async void ImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanImport) return;
        try
        {
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (provider is null) { vm.ImportMessage = "Не удалось открыть файл конфигурации."; return; }
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L.T("Импорт конфигурации"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(L.T("Конфигурации VPN")) { Patterns = ["*.conf", "*.txt", "*.json"] }, FilePickerFileTypes.All]
            });
            if (files.Count == 0 || !vm.CanImport || !vm.IsImportOpen) return;
            await using var stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(block)) != 0)
            {
                if (buffer.Length + read > ProfileParser.MaximumBytes) throw new FormatException("Не удалось импортировать: конфигурация превышает 2 МБ.");
                buffer.Write(block, 0, read);
            }
            vm.ImportText = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            vm.ImportName = Path.GetFileNameWithoutExtension(files[0].Name);
            await ImportAndUpdateNavigationAsync(vm);
        }
        catch (FormatException error) { vm.ImportMessage = error.Message; }
        catch { vm.ImportMessage = "Не удалось открыть файл конфигурации."; }
    }

    private void ManualImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.OpenManualImportCommand.Execute(null);
        AppBackNavigation.Set(() => ReturnToImportChoices(vm));
    }

    private async void ManualImportSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) await ImportAndUpdateNavigationAsync(vm);
    }

    private void ImportBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CanImport) CloseImport(vm);
    }

    private static void ImportCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private static async Task ImportAndUpdateNavigationAsync(MainViewModel vm)
    {
        await vm.ImportCommand.ExecuteAsync(null);
        if (!vm.IsImportOpen) AppBackNavigation.Clear();
    }

    private static void CloseImport(MainViewModel vm)
    {
        vm.CloseImportCommand.Execute(null);
        if (!vm.IsImportOpen) AppBackNavigation.Clear();
    }

    private static void ReturnToImportChoices(MainViewModel vm)
    {
        if (!vm.IsImportOpen) { AppBackNavigation.Clear(); return; }
        vm.IsManualImportOpen = false;
        vm.ImportMessage = "";
        AppBackNavigation.Set(() => CloseImport(vm));
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
