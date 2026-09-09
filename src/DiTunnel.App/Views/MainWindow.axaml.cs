using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Views;

public partial class MainWindow : Window
{
    private bool canClose, closing, ready;
    private TrayIcon? tray;
    private WindowPlacement? normal;
    private readonly DispatcherTimer trayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NativeMenuItem? trayStatus, showItem, connectItem, exitItem;
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (ready) return;
            var saved = UserSettings.Current.Window;
            var screen = saved is null ? Screens.Primary : Screens.ScreenFromPoint(new PixelPoint(saved.X, saved.Y)) ?? Screens.Primary;
            if (screen is not null)
            {
                normal = WindowLayout.Fit(saved, screen.WorkingArea, screen.Scaling);
                MinWidth = Math.Min(360, normal.Width); MinHeight = Math.Min(500, normal.Height);
                Width = normal.Width; Height = normal.Height; Position = new PixelPoint(normal.X, normal.Y);
                if (normal.Maximized) WindowState = WindowState.Maximized;
            }
            ready = true; SetupTray();
            if (DataContext is MainViewModel vm) await vm.RefreshSubscriptionsAsync();
        };
        PositionChanged += (_, _) => RememberNormal();
        SizeChanged += (_, _) => RememberNormal();
        Closing += async (_, e) =>
        {
            if (canClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            var action = UserSettings.Current.CloseAction;
            if (action == "ask") action = await Dialogs.AskClose(this);
            if (action == "hide" && tray is not null)
            {
                SavePlacement(); Hide(); closing = false; return;
            }
            if (action == "hide" && DataContext is MainViewModel model) model.Notice = "Трей недоступен. Выберите «Выйти» в настройках.";
            if (action == "exit") await ExitAsync();
            else closing = false;
        };
        Closed += (_, _) => { trayTimer.Stop(); tray?.Dispose(); L.Changed -= LanguageChanged; };
        L.Changed += LanguageChanged;
    }
    private void LanguageChanged()
    {
        if (DataContext is MainViewModel vm) vm.RefreshLanguage();
        UpdateTray();
    }
    private void RememberNormal()
    {
        if (ready && IsVisible && WindowState == WindowState.Normal)
            normal = new(Position.X, Position.Y, Bounds.Width, Bounds.Height, false);
    }
    private void SavePlacement()
    {
        if (normal is null) return;
        UserSettings.Current.Window = normal with { Maximized = WindowState == WindowState.Maximized };
        try { UserSettings.Current.Save(); }
        catch { if (DataContext is MainViewModel vm) vm.Notice = "Не удалось сохранить настройки."; }
    }
    private void SetupTray()
    {
        try
        {
            trayStatus = new NativeMenuItem { IsEnabled = false };
            showItem = new NativeMenuItem(); showItem.Click += (_, _) => Restore();
            connectItem = new NativeMenuItem(); connectItem.Click += (_, _) =>
            {
                Restore();
                if (DataContext is MainViewModel vm && vm.CanConnect) vm.ConnectCommand.Execute(null);
            };
            exitItem = new NativeMenuItem(); exitItem.Click += async (_, _) => { if (!closing) { closing = true; await ExitAsync(); } };
            tray = new TrayIcon { Icon = Icon, IsVisible = true, Menu = new NativeMenu { Items = { trayStatus, new NativeMenuItemSeparator(), showItem, connectItem, exitItem } } };
            tray.Clicked += (_, _) => Restore();
            trayTimer.Tick += (_, _) => UpdateTray(); trayTimer.Start(); UpdateTray();
        }
        catch { tray?.Dispose(); tray = null; }
    }
    private void Restore()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void UpdateTray()
    {
        if (tray is null || DataContext is not MainViewModel vm) return;
        var disconnected = vm.ConnectionState == DiTunnel.Core.Connection.VpnConnectionState.Disconnected;
        var status = disconnected
            ? L.T("Нажмите для подключения")
            : L.T(vm.StatusText) + "\n" + vm.SelectedName + "\n" + L.T("Последняя проверка:") + " " + L.T(vm.SelectedProfile?.ProbeText ?? "Не проверен");
        var tooltip = "Di-Tunnel — " + status;
        tray.ToolTipText = tooltip[..Math.Min(127, tooltip.Length)];
        trayStatus!.Header = status.Replace('\n', ' ');
        showItem!.Header = L.T("Открыть Di-Tunnel");
        connectItem!.Header = L.T(disconnected ? "Подключить" : "Отключить");
        connectItem.IsEnabled = vm.CanConnect;
        exitItem!.Header = L.T("Выйти");
    }
    private async Task ExitAsync()
    {
        SavePlacement(); IsEnabled = false;
        if (DataContext is MainViewModel vm) await vm.PrepareToCloseAsync(TimeSpan.FromSeconds(8));
        canClose = true; Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
