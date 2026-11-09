using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiTunnel.App;
using DiTunnel.App.Views;

internal static class WindowsCaptionSmoke
{
    [SupportedOSPlatform("windows")]
    internal static void RunManaged()
    {
        var closeAction = UserSettings.Current.CloseAction;
        UserSettings.Current.CloseAction = "exit";
        var integration = new MainWindow(enableShellHook: false, useManagedCaption: true);
        Require(integration.Content is ApplicationShell && integration.ExtendClientAreaToDecorationsHint
            && integration.WindowDecorations == WindowDecorations.Full, "shared Windows constructor retains its navigation shell");
        integration.Close(); Dispatcher.UIThread.RunJobs();
        UserSettings.Current.CloseAction = closeAction;
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        var window = new Window { Title = "Synthetic settings", Width = 640, Height = 420,
            Icon = new WindowIcon(stream),
            Position = new(-32000, -32000), ShowActivated = false, ShowInTaskbar = false };
        WindowsWindowChrome.Apply(window);
        window.Content = new Border { Background = Brushes.Gray };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var root = window.GetVisualAncestors().LastOrDefault() ?? window;
        var bounds = WindowsWindowBounds.Capture(window.TryGetPlatformHandle()!.Handle);
        var foreground = GetForegroundWindow();
        var caption = root.GetVisualDescendants().OfType<Panel>().Single(item => item.Name == "PART_TitleBar");
        var title = root.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Parent is Panel panel && panel.Name == "PART_TitleTextPanel");
        var buttons = root.GetVisualDescendants().OfType<Button>().Where(item => item.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton").ToArray();
        Require(buttons.Length == 3, "all managed caption buttons");
        Require(window.Padding.Top == 32 && window.WindowDecorationMargin.Top == 32, "content inset exactly once below caption");
        var clientOrigin = new NativePoint();
        ClientToScreen(window.TryGetPlatformHandle()!.Handle, ref clientOrigin);
        Require(bounds is { } outer && clientOrigin.Y == outer.Y, "no uncovered non-client top stripe");
        Directory.CreateDirectory(".tools/caption-preview");
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var dark = index % 2 == 0;
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var captionColour = ((ISolidColorBrush)caption.Background!).Color;
                Require(dark ? captionColour.R < 80 : captionColour.R > 200, "caption background changes synchronously");
                window.Title = "Synthetic page " + index;
                Dispatcher.UIThread.RunJobs();
                Require(title.Text == window.Title, "page title binding");
                foreach (var button in buttons)
                {
                    Require(button.IsEffectivelyVisible && button.IsEnabled && button.Bounds.Width > 0, "caption buttons remain usable");
                    var glyph = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                    Require(glyph.Data!.Bounds.Width > 0 && glyph.Data.Bounds.Height > 0, "caption glyph has renderable geometry");
                    var colour = ((ISolidColorBrush)glyph.Fill!).Color;
                    Require(dark ? colour.R > 180 : colour.R < 100, "caption glyph contrast");
                    var box = button.GetVisualDescendants().OfType<Viewbox>().Single();
                    var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), button)!.Value;
                    Require(Math.Abs(center.Y - button.Bounds.Height / 2) < 1.5, "caption glyph vertically centered");
                }
                Require(WindowsWindowBounds.Capture(window.TryGetPlatformHandle()!.Handle) == bounds, "outer bounds unchanged");
                Require(GetForegroundWindow() == foreground, "theme changes do not activate window");
                if (index < 2)
                {
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
                    bitmap.Render(root); bitmap.Save($".tools/caption-preview/managed-{(dark ? "dark" : "light")}.png");
                }
            }
            var closingAttempted = false;
            void CancelClose(object? sender, WindowClosingEventArgs args) { closingAttempted = true; args.Cancel = true; }
            window.Closing += CancelClose;
            buttons.Single(button => button.Name == "PART_CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(closingAttempted && window.IsVisible, "close button honours normal cancellable window-closing flow");
            window.Closing -= CancelClose;
            Console.WriteLine("PASS_UNIFIED_CAPTION_8_THEME_SWITCHES_CENTERED_BUTTONS_NO_TOP_STRIPE_TITLE_CLOSE_BOUNDS_NO_ACTIVATION");
        }
        finally { window.Close(); }
    }

    [SupportedOSPlatform("windows")]
    internal static void Run()
    {
        var window = new Window { Title = "Di-Tunnel isolated caption check", Width = 640, Height = 540,
            Position = new(-32000, -32000), ShowActivated = false, ShowInTaskbar = false };
        window.ActualThemeVariantChanged += (_, _) => DesktopWindowStyle.Update(window);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var handle = window.TryGetPlatformHandle()!.Handle;
            var bounds = WindowsWindowBounds.Capture(handle);
            var style = GetWindowLongPtr(handle, -16);
            var foreground = GetForegroundWindow();
            Require((style.ToInt64() & 0xcb0000) == 0xcb0000, "native caption, system menu, minimize and maximize styles");
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var dark = iteration % 2 == 0;
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                Dispatcher.UIThread.RunJobs();
                DesktopWindowStyle.Update(window);
                Require(DwmGetWindowAttribute(handle, 20, out var value, sizeof(int)) == 0 && value == (dark ? 1 : 0), "immediate native theme");
                Require(WindowsWindowBounds.Capture(handle) == bounds, "unchanged physical outer bounds");
                Require(GetWindowLongPtr(handle, -16) == style, "unchanged native window styles");
                Require(GetForegroundWindow() == foreground, "theme refresh does not activate the test window");
                CheckCaptionButtons(handle);
            }
            Console.WriteLine("PASS_NATIVE_CAPTION_8_THEME_SWITCHES_BUTTONS_BOUNDS_STYLES_NO_ACTIVATION");
        }
        finally { window.Close(); }
    }

    private static void CheckCaptionButtons(IntPtr handle)
    {
        // TITLEBARINFOEX: cbSize, title RECT, six states, six button RECTs.
        const int size = 140;
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteInt32(info, size);
            SendMessage(handle, 0x033f, IntPtr.Zero, info);
            foreach (var index in new[] { 2, 3, 5 })
            {
                var state = Marshal.ReadInt32(info, 20 + index * 4);
                var rect = 44 + index * 16;
                Require((state & 0x8001) == 0, "caption button visible and available");
                Require(Marshal.ReadInt32(info, rect + 8) > Marshal.ReadInt32(info, rect)
                    && Marshal.ReadInt32(info, rect + 12) > Marshal.ReadInt32(info, rect + 4), "nonempty caption button bounds");
            }
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
