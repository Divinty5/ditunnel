using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using DiTunnel.App.Views;
using DiTunnel.App.ViewModels;
using DiTunnel.App;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

internal static class Program
{
    private static readonly string TestApplicationId = "DiVintyInteractive.DiTunnel.Smoke." + Guid.NewGuid().ToString("N");
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string[] args)
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var settingsPath = Path.Combine(Path.GetTempPath(), "DiTunnel-icon-settings-" + Guid.NewGuid().ToString("N"));
        AppPaths.Configure(settingsPath, settingsPath, settingsPath);
        var builder = AppBuilder.Configure<DiTunnel.App.App>().UsePlatformDetect().WithInterFont();
#pragma warning disable AVALONIA_X11_FORCE_CSD
        if (OperatingSystem.IsLinux()) builder.With(new X11PlatformOptions { ForceDrawnDecorations = true, WmClass = "Di-Tunnel.Linux" });
#pragma warning restore AVALONIA_X11_FORCE_CSD
        builder.SetupWithoutStarting();
        if (OperatingSystem.IsLinux() && args is ["--linux-desktop", var desktopOutput])
        {
            VerifyLinuxDesktop(Path.GetFullPath(desktopOutput));
            return;
        }
        if (OperatingSystem.IsLinux()) VerifyLinuxDockLease();
        if (OperatingSystem.IsWindows())
        {
            Require(ReadProcessApplicationId() == WindowsTaskbarIcon.ApplicationId, "Application identity initialized before creating windows");
            WindowsTaskbarIcon.InitializeProcess(TestApplicationId);
            VerifyLegacyShortcutRepair();
        }
        if (OperatingSystem.IsWindows() && args is ["--windows-ui"])
        {
            UserSettings.Current.CheckForUpdatesAutomatically = false;
            UserSettings.Current.CloseAction = "exit";
            App.Platform = AppPlatform.Windows;
            App.SetWindowsAutostart = _ => { }; // Isolated UI test; never register real logon startup.
            var test = new MainWindow(enableShellHook: false) { DataContext = new MainViewModel(null, new EmptyStore()), Title = "Di-Tunnel · Windows UI test" };
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            test.Closed += (_, _) => lifetime.Cancel();
            test.Show();
            Dispatcher.UIThread.MainLoop(lifetime.Token);
            if (test.IsVisible) test.Close();
            Console.WriteLine("PASS_ISOLATED_WINDOWS_UI_NO_NETWORK_NO_EXPLORER_HOOK_NO_AUTOSTART_REGISTRATION");
            return;
        }
        if (OperatingSystem.IsWindows() && args is ["--windows-startup"])
        {
            UserSettings.Current.CheckForUpdatesAutomatically = false;
            UserSettings.Current.CloseAction = "exit";
            UserSettings.Current.StartWithWindows = true;
            UserSettings.Current.StartMinimized = true;
            foreach (var automatic in new[] { true, false })
            {
                App.IsAutostartLaunch = automatic;
                Require(App.ShouldStartMinimized == automatic, "Only logon startup uses the minimized preference");
                var test = new MainWindow(enableShellHook: false)
                {
                    DataContext = new MainViewModel(null, new EmptyStore()), ShowActivated = !App.ShouldStartMinimized,
                    WindowState = App.ShouldStartMinimized ? WindowState.Minimized : WindowState.Normal
                };
                test.Show(); Dispatcher.UIThread.RunJobs();
                Require(automatic ? !test.IsVisible || test.WindowState == WindowState.Minimized : test.IsVisible && test.WindowState == WindowState.Normal,
                    "Automatic launch minimizes; manual launch opens a visible window");
                if (automatic)
                {
                    test.Show(); test.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
                    Require(test.IsVisible && test.WindowState == WindowState.Normal, "The minimized startup window can be restored");
                }
                using var closed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var completed = false;
                test.Closed += (_, _) => { completed = true; closed.Cancel(); };
                test.Close();
                if (!completed) Dispatcher.UIThread.MainLoop(closed.Token);
                Require(completed, "Startup test closed and disposed its tray");
            }
            Console.WriteLine("PASS_WINDOWS_AUTOSTART_MINIMIZED_MANUAL_VISIBLE_AND_RESTORE_NO_REGISTRATION");
            return;
        }
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        var original = new WindowIcon(stream);
        var window = new Window { Icon = original };
        VerifyWindowThemeAndBounds(window);
        if (args is ["--render-settings", var output])
        {
            VerifySettingsPresentation(window, Path.GetFullPath(output));
            VerifyMainCountryFlag(window, Path.GetFullPath(output));
            VerifySubscriptionsScrolling(window);
            if (OperatingSystem.IsWindows()) VerifyTaskbarRefresh(window);
        }
        var draft = new TextBox { Text = "ru\nрф" };
        var section = Dialogs.SettingsSection("Соединение", draft);
        Require(!section.IsExpanded, "Settings sections start collapsed");
        section.IsExpanded = true; draft.Text = "example.ru"; section.IsExpanded = false;
        Require(ReferenceEquals(section.Content, draft) && draft.Text == "example.ru", "Collapsing keeps settings drafts");
        var model = new MainViewModel(null, new EmptyStore());
        window.DataContext = model;
        using var indicator = new VpnWindowIconIndicator(window, enableShellHook: false);
        original = window.Icon!;
        // Linux runs this test only under Xvfb; Windows never shows a test window.
        if (OperatingSystem.IsLinux()) window.Show();
        Require(ReferenceEquals(window.Icon, original), "Disconnected icon");
        var purple = ReadX11Icon(window);
        var purpleWindows = ReadWindowsIcon(window);
        model.ConnectionState = VpnConnectionState.Connected;
        var green = window.Icon;
        Require(!ReferenceEquals(green, original), "Connected icon");
        var greenPixels = ReadX11Icon(window);
        var greenWindows = ReadWindowsIcon(window);
        model.ConnectionState = VpnConnectionState.Error;
        var red = window.Icon;
        Require(!ReferenceEquals(red, green) && !ReferenceEquals(red, original), "Error icon");
        var redPixels = ReadX11Icon(window);
        var redWindows = ReadWindowsIcon(window);
        if (OperatingSystem.IsWindows())
        {
            Require(ReadShellString(window, 5) is null && ReadShellString(window, 3) is null && ReadShellString(window, 2) is null,
                "Native window icon does not set relaunch properties");
            Require(!purpleWindows.SequenceEqual(greenWindows) && !greenWindows.SequenceEqual(redWindows), "Native Windows large icon updates");
            Require(greenWindows.Count(pixel => Channel(pixel, 8) > Channel(pixel, 16) + 20 && Channel(pixel, 8) > Channel(pixel, 0) + 20) > 10, "Windows green tunnel");
            Require(redWindows.Count(pixel => Channel(pixel, 16) > Channel(pixel, 8) + 20 && Channel(pixel, 16) > Channel(pixel, 0) + 20) > 10, "Windows red tunnel");
        }
        if (OperatingSystem.IsLinux())
        {
            Require(purple.Length > 2 && greenPixels.Length == purple.Length && redPixels.Length == purple.Length, "X11 icon dimensions");
            Require(!purple.SequenceEqual(greenPixels) && !greenPixels.SequenceEqual(redPixels), "Native X11 icon updates");
            Require(greenPixels.Skip(2).Count(pixel => Channel(pixel, 8) > Channel(pixel, 16) + 20 && Channel(pixel, 8) > Channel(pixel, 0) + 20) > 100, "Green tunnel");
            Require(redPixels.Skip(2).Count(pixel => Channel(pixel, 16) > Channel(pixel, 8) + 20 && Channel(pixel, 16) > Channel(pixel, 0) + 20) > 100, "Red tunnel");
        }
        foreach (var state in new[] { VpnConnectionState.Connecting, VpnConnectionState.Reconnecting, VpnConnectionState.Disconnecting, VpnConnectionState.Disconnected })
        {
            model.ConnectionState = state;
            Require(ReferenceEquals(window.Icon, original), "Purple icon for " + state);
        }
        model.ConnectionState = VpnConnectionState.Connected;
        Require(ReferenceEquals(window.Icon, green), "Icon cache");
        var replacement = new MainViewModel(null, new EmptyStore()) { ConnectionState = VpnConnectionState.Error };
        window.DataContext = replacement;
        Require(ReferenceEquals(window.Icon, red), "DataContext replacement");
        indicator.Dispose(); indicator.Dispose();
        replacement.ConnectionState = VpnConnectionState.Connected;
        Require(ReferenceEquals(window.Icon, red), "Disposal unsubscribes state changes");
        window.Close();
        Console.WriteLine(OperatingSystem.IsLinux() ? "PASS_NATIVE_X11_VPN_ICON_COLOURS_CACHE_DISPOSAL" : "PASS_NATIVE_WINDOWS_VPN_ICON_COLOURS_CACHE_DISPOSAL");
    }

    private static void VerifySettingsPresentation(Window window, string output)
    {
        Directory.CreateDirectory(output);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        var drafts = new List<TextBox>();
        var sections = new List<Expander>();
        foreach (var title in new[] { "Интерфейс", "Соединение", "Раздельное туннелирование", "Диагностика", "Обновления" })
        {
            var draft = new TextBox { Text = "example.ru" };
            drafts.Add(draft);
            var content = new StackPanel { Spacing = 14, Margin = new Thickness(0, 12, 0, 4) };
            content.Children.Add(new TextBlock { Text = "Настройки раздела" });
            content.Children.Add(draft);
            var section = Dialogs.SettingsSection(title, content);
            sections.Add(section);
            panel.Children.Add(section);
        }
        var root = new Border { Child = panel };
        root.Bind(Border.BackgroundProperty, root.GetResourceObservable("PageBrush"));
        window.Content = root;
        window.ShowInTaskbar = false;
        window.Width = 900; window.Height = 850;
        window.Show();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            foreach (var width in new[] { 900, 360 })
            {
                window.Width = width;
                foreach (var expanded in new[] { false, true })
                {
                    foreach (var section in sections) section.IsExpanded = expanded;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    foreach (var section in sections)
                    {
                        var toggle = section.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "PART_HeaderSite");
                        var presenter = section.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_ContentSite");
                        var arrow = toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                        var heading = (TextBlock)section.Header!;
                        Require(toggle.IsChecked == expanded && presenter.IsVisible == expanded, "Settings header and content track expansion");
                        Require(arrow.TranslatePoint(default, section)!.Value.X >= heading.TranslatePoint(default, section)!.Value.X + heading.Bounds.Width,
                            "Settings chevron follows the title");
                        Require(arrow.TranslatePoint(default, section)!.Value.X + arrow.Bounds.Width <= section.Bounds.Width + 1,
                            "Settings chevron fits narrow screens");
                        Require(drafts.All(draft => draft.Text == "example.ru"), "Settings drafts survive theme and expansion changes");
                    }
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
                    bitmap.Render(root);
                    bitmap.Save(Path.Combine(output, $"settings-{theme}-{width}-{(expanded ? "expanded" : "collapsed")}.png"), new PngBitmapEncoderOptions());
                }
            }
        }
        var first = sections[0];
        var header = first.GetVisualDescendants().OfType<ToggleButton>().Single();
        header.IsChecked = false;
        Require(!first.IsExpanded, "Header collapses settings");
        header.IsChecked = true;
        Require(first.IsExpanded, "Header expands settings");
        header.Focus();
        header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
        Require(!first.IsExpanded, "Space key collapses settings");
        window.Hide();
        window.Content = null;
        Console.WriteLine("PASS_MINIMAL_SETTINGS_DARK_LIGHT_DESKTOP_NARROW_EXPANSION_DRAFTS");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyTaskbarRefresh(Window window)
    {
        // Native icon checks run without loading code into Explorer.
        using (var taskbar = new WindowsTaskbarIcon(window, enableShellHook: false))
        {
            window.Icon = VpnWindowIconIndicator.CreateIcon(null);
            taskbar.Update(window.Icon);
            window.ShowInTaskbar = true;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var handle = window.TryGetPlatformHandle()!.Handle;
            var bounds = WindowsWindowBounds.Capture(handle);
            foreach (var state in new[] { VpnConnectionState.Connected, VpnConnectionState.Error, VpnConnectionState.Disconnected })
            {
                window.Icon = VpnWindowIconIndicator.CreateIcon(state == VpnConnectionState.Disconnected ? null : ConnectionPalette.AccentFor(state));
                var foreground = GetForegroundWindow();
                taskbar.Update(window.Icon);
                Dispatcher.UIThread.RunJobs();
                var before = ReadWindowsIcon(window);
                var oldIcon = SendMessage(handle, 0x7f, new IntPtr(1), IntPtr.Zero);
                taskbar.RefreshCurrentIcon(); // Simulate symbols finishing after a state change.
                Dispatcher.UIThread.RunJobs();
                Require(SendMessage(handle, 0x7f, new IntPtr(1), IntPtr.Zero) != oldIcon, "Late attachment replaces the native icon handle");
                Require(ReadWindowsIcon(window).SequenceEqual(before), "Late attachment replays the current colour");
                Require(window.IsVisible && WindowsWindowBounds.Capture(handle) == bounds, "Taskbar refresh preserves visible window and bounds");
                Require(GetForegroundWindow() == foreground, "Taskbar refresh preserves foreground window");
                Require(ReadShellString(window, 5) is null && ReadShellString(window, 3) is null, "Taskbar refresh uses the native window icon");
                Require(ReadProcessApplicationId() == TestApplicationId, "Taskbar refresh preserves process group identity");
                Require(window.TryGetPlatformHandle()!.Handle == handle && window.ShowInTaskbar, "Taskbar refresh keeps the existing window registered");
            }
            window.Hide();
            window.ShowInTaskbar = false;
        }


        Console.WriteLine("PASS_VISIBLE_WINDOWS_NATIVE_ICON_BOUNDS_FOCUS_SAME_WINDOW_NO_RELAUNCH_OVERRIDE");
    }

    private sealed class SyntheticCountryResolver : IServerCountryResolver
    {
        public Task<string?> ResolveAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.FromResult<string?>("SE");
    }

    private static void VerifyMainCountryFlag(Window window, string output)
    {
        var model = new MainViewModel(null, new ManyServersStore(), countryResolver: new SyntheticCountryResolver(), platform: AppPlatform.Linux,
            selectionSettings: new(), persistSelection: () => { });
        var view = new MainView { DataContext = model };
        window.Content = view;
        window.Width = 920; window.Height = 900; window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Require(model.HasSelectedFlag && view.GetVisualDescendants().OfType<Image>().Any(image =>
            image.IsEffectivelyVisible && ReferenceEquals(image.Source, model.SelectedFlag) && image.Bounds.Width > 0), "Resolved country flag is visible on the main screen");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height));
        bitmap.Render(view);
        bitmap.Save(Path.Combine(output, "main-country-flag.png"), new PngBitmapEncoderOptions());
        window.Hide(); window.Content = null;
        model.ShutdownAsync().GetAwaiter().GetResult();
        Console.WriteLine("PASS_MAIN_COUNTRY_FLAG_RENDERED");
    }

    private static void VerifySubscriptionsScrolling(Window window)
    {
        foreach (var platform in new[] { AppPlatform.Windows, AppPlatform.Android, AppPlatform.Linux })
        {
            var model = new MainViewModel(null, new ManyServersStore(), new PendingProbe(), platform: platform);
            Dialogs.Subscriptions(window, model).GetAwaiter().GetResult();
            window.ShowInTaskbar = false;
            window.Width = 420; window.Height = 850; window.Show();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var selected = model.SelectedProfile;
            var checking = model.ProbeAllCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Require(model.IsProbing && !model.CanSelectProfile, "Probe is pending and profile selection is blocked");
            Require(list.IsEffectivelyEnabled && scroll.IsEffectivelyEnabled, "Server list remains scrollable during probes");
            Require(!list.GetVisualDescendants().OfType<ListBoxItem>().First().IsEffectivelyEnabled, "Server rows cannot be selected during probes");
            Require(scroll.Extent.Height > scroll.Viewport.Height, "Server list has scrollable content");
            scroll.Offset = new Vector(0, 100);
            Require(scroll.Offset.Y > 0, "Server list offset changes during probes");
            list.SelectedItem = model.Profiles[1];
            Require(ReferenceEquals(selected, model.SelectedProfile) && ReferenceEquals(selected, list.SelectedItem), "Blocked selection preserves the active profile");
            model.CancelProbesCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!checking.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            Require(checking.IsCompleted, "Synthetic probe cancellation completes");
            checking.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
            Require(model.CanSelectProfile && list.GetVisualDescendants().OfType<ListBoxItem>().First().IsEffectivelyEnabled, "Selection reenabled after probes");
            list.SelectedItem = model.Profiles[1];
            Require(ReferenceEquals(model.SelectedProfile, model.Profiles[1]), "User selection works after probes");
            model.Profiles[1].ProbeText = "HTTP · 428 мс";
            model.Profiles[1].ProbeMilliseconds = 428;
            model.Profiles[1].CountryCode = "SE";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var latency = list.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == L.T("428 мс · HTTP"));
            var measurement = new TextBlock { Text = latency.Text, FontSize = latency.FontSize, FontFamily = latency.FontFamily };
            measurement.Measure(Size.Infinity);
            Require(latency.Bounds.Width >= measurement.DesiredSize.Width - 1, "Numeric latency and actual method are visible in a narrow server tile");
            Require(model.Profiles[1].HasFlag && list.GetVisualDescendants().OfType<Image>().Any(image => image.IsEffectivelyVisible && ReferenceEquals(image.Source, model.Profiles[1].FlagImage)), "Country flag is visible next to the probed server");
            window.Hide(); window.Content = null;
            model.ShutdownAsync().GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS_SUBSCRIPTIONS_SCROLL_DURING_PROBES_WINDOWS_ANDROID_LINUX_SELECTION_GUARDS");
    }

    private static void VerifyLinuxDesktop(string output)
    {
        Directory.CreateDirectory(output);
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        var model = new MainViewModel(null, new EmptyStore());
        var label = new TextBlock { Margin = new Thickness(24), FontSize = 24 };
        var window = new Window { Title = "Di-Tunnel · Linux desktop test", Icon = new WindowIcon(stream),
            Width = 700, Height = 500, Content = label, DataContext = model };
        using var indicator = new VpnWindowIconIndicator(window);
        window.Show();
        var states = new[] { VpnConnectionState.Disconnected, VpnConnectionState.Connected, VpnConnectionState.Error, VpnConnectionState.Disconnected };
        for (var index = 0; index < states.Length; index++)
        {
            window.RequestedThemeVariant = index % 2 == 0 ? ThemeVariant.Light : ThemeVariant.Dark;
            model.ConnectionState = states[index];
            label.Text = $"{states[index]} · {window.RequestedThemeVariant}";
            var readyAt = DateTime.UtcNow.AddSeconds(5);
            var stopAt = DateTime.UtcNow.AddSeconds(35);
            var ready = Path.Combine(output, $"{index}.ready");
            var next = Path.Combine(output, $"{index}.continue");
            while (!File.Exists(next) && DateTime.UtcNow < stopAt)
            {
                Dispatcher.UIThread.RunJobs(); Thread.Sleep(10);
                if (DateTime.UtcNow >= readyAt && !File.Exists(ready)) File.WriteAllText(ready, states[index].ToString());
            }
            Require(File.Exists(next), "GNOME screenshot collector acknowledged " + index);
        }
        indicator.Dispose();
        window.Close();
        Console.WriteLine("PASS_LINUX_DESKTOP_PINNED_ICON_COLOURS_AND_THEME_CAPTURED");
    }

    private static void VerifyLinuxDockLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "ditunnel-dock-lease-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var state = Path.Combine(root, "state");
        var path = Path.Combine(data, "icons/hicolor/256x256/apps/di-tunnel.png");
        var desktop = Path.Combine(data, "applications/di-tunnel.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
        using var bytes = new MemoryStream();
        VpnWindowIconIndicator.CreateIcon(null).Save(bytes);
        var original = bytes.ToArray();
        File.WriteAllBytes(path, original);
        File.WriteAllText(desktop, "[Desktop Entry]\nType=Application\nName=Custom Di-Tunnel\nExec=di-tunnel\nIcon=di-tunnel\n");
        var desktopBytes = File.ReadAllBytes(desktop);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(desktop, mode);
        try
        {
            var abandoned = new LinuxDockIcon(data, state);
            abandoned.Update(VpnWindowIconIndicator.CreateIcon(ConnectionPalette.AccentFor(VpnConnectionState.Connected)));
            Require(!File.ReadAllBytes(path).SequenceEqual(original), "Dock icon is recoloured");
            using (var recovered = new LinuxDockIcon(data, state))
            {
                Require(File.ReadAllBytes(path).SequenceEqual(original), "A stale lease restores the original icon after a crash");
                recovered.Update(VpnWindowIconIndicator.CreateIcon(ConnectionPalette.AccentFor(VpnConnectionState.Error)));
            }
            Require(File.ReadAllBytes(path).SequenceEqual(original), "Normal disposal restores a custom icon");
            Require(File.ReadAllBytes(desktop).SequenceEqual(desktopBytes), "Custom desktop entry is preserved");
            if (OperatingSystem.IsLinux()) Require(File.GetUnixFileMode(desktop) == mode, "Custom desktop entry permissions are preserved");
            using (var edited = new LinuxDockIcon(data, state))
            {
                edited.Update(VpnWindowIconIndicator.CreateIcon(ConnectionPalette.AccentFor(VpnConnectionState.Connected)));
                File.WriteAllText(path, "user edit");
            }
            Require(File.ReadAllText(path) == "user edit", "User edits made during execution are preserved");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS_LINUX_DOCK_CUSTOM_ICON_LEASE_CRASH_RECOVERY_PERMISSIONS_AND_USER_EDITS");
    }

    private sealed class ManyServersStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => Enumerable.Range(1, 30)
            .Select(index => new ImportedProfile("Test " + index, "VLESS", "synthetic-" + index, "test", "Test subscription")).ToArray();
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class PendingProbe : IServerProbe
    {
        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ServerProbeResult(null, "synthetic");
        }
    }

    private static void VerifyWindowThemeAndBounds(Window window)
    {
        var handle = window.TryGetPlatformHandle()!.Handle;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            DesktopWindowStyle.Update(window);
            if (OperatingSystem.IsWindows())
            {
                Require(DwmGetWindowAttribute(handle, 20, out var dark, sizeof(int)) == 0, "Native caption theme attribute");
                Require(dark == (theme == ThemeVariant.Dark ? 1 : 0), "Native caption theme updates immediately");
            }
            else if (OperatingSystem.IsLinux()) Require(ReadThemeHint(window) == (theme == ThemeVariant.Dark ? "dark" : "light"), "X11 caption theme hint updates immediately");
        }
        if (OperatingSystem.IsWindows())
        {
            var original = WindowsWindowBounds.Capture(handle)!.Value;
            WindowsWindowBounds.Restore(handle, new PixelRect(original.X, original.Y, original.Width, Math.Max(100, original.Height - 40)));
            WindowsWindowBounds.Restore(handle, original);
            Require(WindowsWindowBounds.Capture(handle) == original, "Exact physical outer bounds preserved");
        }
        Console.WriteLine("PASS_SETTINGS_SECTIONS_NATIVE_THEME_AND_WINDOW_BOUNDS");
    }

    private static string ReadThemeHint(Window window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        try
        {
            var atom = XInternAtom(display, "_GTK_THEME_VARIANT", false);
            var result = XGetWindowProperty(display, window.TryGetPlatformHandle()!.Handle, atom, 0, 64,
                false, IntPtr.Zero, out _, out var format, out var count, out _, out var data);
            try
            {
                Require(result == 0 && format == 8 && count is > 0 and < 64, "X11 caption hint format");
                return Marshal.PtrToStringUTF8(data, (int)count)!;
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    private static int Channel(ulong pixel, int shift) => (int)(pixel >> shift & 255);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [SupportedOSPlatform("windows")]
    private static void VerifyLegacyShortcutRepair()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiTunnel-launcher-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cached = Path.Combine(directory, "taskbar.ico");
        var target = Environment.ProcessPath!;
        var ours = Path.Combine(directory, "ours.lnk");
        var foreign = Path.Combine(directory, "foreign.lnk");
        var custom = Path.Combine(directory, "custom.lnk");
        try
        {
            CreateShortcut(ours, target, (cached, 0));
            CreateShortcut(foreign, Path.Combine(directory, "other.exe"), (cached, 0));
            CreateShortcut(custom, target, (Path.Combine(directory, "custom.ico"), 3));
            Require(WindowsShellShortcut.RepairLegacyIcon(ours, directory, target), "Legacy launcher migrated");
            Require(ReadShortcutIcon(ours) == (target, 0), "Idle launcher uses original application icon");
            Require(!WindowsShellShortcut.RepairLegacyIcon(ours, directory, target), "Migration is idempotent");
            Require(!WindowsShellShortcut.RepairLegacyIcon(foreign, directory, target), "Foreign launcher remains unchanged");
            Require(ReadShortcutIcon(foreign) == (cached, 0), "Foreign icon preserved");
            Require(!WindowsShellShortcut.RepairLegacyIcon(custom, directory, target), "Custom icon remains unchanged");
            Require(ReadShortcutIcon(custom) == (Path.Combine(directory, "custom.ico"), 3), "Custom resource index preserved");
        }
        finally
        {
            foreach (var path in new[] { ours, foreign, custom }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("PASS_LEGACY_LAUNCHER_MIGRATION_FOREIGN_CUSTOM_ISOLATION_IDEMPOTENCE");
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string path, string target, (string Path, int Index) icon)
    {
        using var shortcut = new WindowsShellShortcut { Target = target, Icon = icon };
        shortcut.Save(path);
    }

    [SupportedOSPlatform("windows")]
    private static (string Path, int Index) ReadShortcutIcon(string path)
    {
        using var shortcut = new WindowsShellShortcut();
        shortcut.Load(path);
        return shortcut.Icon;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadProcessApplicationId()
    {
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var value));
        try { return Marshal.PtrToStringUni(value); }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadShellString(Window window, uint id)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window.TryGetPlatformHandle()!.Handle, ref iid, out var store));
        try
        {
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { if (value.Type == 0) return null; Require(value.Type == 31, "Shell property string ABI"); return Marshal.PtrToStringUni(value.Pointer); }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr applicationId);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyVariant value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private static ulong[] ReadWindowsIcon(Window window)
    {
        if (!OperatingSystem.IsWindows()) return [];
        Dispatcher.UIThread.RunJobs();
        var icon = SendMessage(window.TryGetPlatformHandle()!.Handle, 0x7f, new IntPtr(1), IntPtr.Zero);
        Require(icon != IntPtr.Zero, "Windows ICON_BIG");
        return ReadIconPixels(icon);
    }

    private static ulong[] ReadIconPixels(IntPtr icon)
    {
        Require(GetIconInfo(icon, out var info), "Windows icon bitmap");
        try
        {
            Require(GetObject(info.Color, Marshal.SizeOf<NativeBitmap>(), out var bitmap) != 0, "Windows icon size");
            Require(bitmap.Width is >= 16 and <= 64 && bitmap.Height == bitmap.Width, "Native taskbar icon size");
            var header = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 };
            var pixels = new uint[bitmap.Width * bitmap.Height];
            var dc = GetDC(IntPtr.Zero);
            try { Require(GetDIBits(dc, info.Color, 0, (uint)bitmap.Height, pixels, ref header, 0) == bitmap.Height, "Windows icon pixels"); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            Console.WriteLine($"Windows icon {bitmap.Width}x{bitmap.Height}: {pixels.Distinct().Count()} colours");
            return pixels.Select(pixel => (ulong)pixel).ToArray();
        }
        finally { DeleteObject(info.Color); DeleteObject(info.Mask); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int Icon; public uint X, Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, uint[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr value, int size, out NativeBitmap bitmap);

    private static ulong[] ReadX11Icon(Window window)
    {
        if (!OperatingSystem.IsLinux()) return [];
        Dispatcher.UIThread.RunJobs();
        // The test has no running dispatcher loop to flush Avalonia's X11
        // connection, so explicitly synchronize it before using a second one.
        var implementation = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window)!;
        var globals = implementation.GetType().GetField("_x11", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(implementation)!;
        var displayField = globals.GetType().GetField("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var windowDisplay = (IntPtr)(displayField?.GetValue(globals)
            ?? globals.GetType().GetProperty("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(globals))!;
        XSync(windowDisplay, false);
        var display = XOpenDisplay(IntPtr.Zero);
        Require(display != IntPtr.Zero, "X11 display");
        try
        {
            var atom = XInternAtom(display, "_NET_WM_ICON", false);
            var status = XGetWindowProperty(display, window.TryGetPlatformHandle()!.Handle, atom, 0, 100000,
                false, IntPtr.Zero, out _, out var format, out var count, out _, out var data);
            try
            {
                Require(status == 0 && format == 32 && count > 2 && count <= 100000, "X11 _NET_WM_ICON property");
                return Enumerable.Range(0, (int)count).Select(index => (ulong)Marshal.ReadInt64(data, index * IntPtr.Size)).ToArray();
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }
        finally { XCloseDisplay(display); }
    }

    private sealed class EmptyStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr requestedType,
        out IntPtr actualType, out int actualFormat, out ulong count, out ulong bytesAfter, out IntPtr data);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
