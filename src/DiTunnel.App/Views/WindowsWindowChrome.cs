using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;

namespace DiTunnel.App.Views;

internal static class WindowsWindowChrome
{
    internal static void Apply(Window window)
    {
        // Avalonia 12 owns the entire title bar on both Windows 10 and 11. Full
        // decorations + extension retain native resize/snap geometry without the
        // uncovered BorderOnly top strip or independently laid-out caption glyphs.
        window.WindowDecorations = WindowDecorations.Full;
        window.ExtendClientAreaTitleBarHeightHint = 32;
        window.ExtendClientAreaToDecorationsHint = true;
        window.Bind(Window.PaddingProperty, new Binding(nameof(Window.WindowDecorationMargin)) { Source = window });
        if (window.FindResource(typeof(WindowDrawnDecorations)) is ControlTheme theme)
        {
            var decorations = new ControlTheme(typeof(WindowDrawnDecorations)) { BasedOn = theme };
            var fullscreen = new Style(selector => selector.Nesting().Template().OfType<Button>().Name("PART_FullScreenButton"));
            fullscreen.Setters.Add(new Setter(Visual.IsVisibleProperty, false));
            decorations.Children.Add(fullscreen);
            var titlePanel = new Style(selector => selector.Nesting().Template().OfType<Panel>().Name("PART_TitleTextPanel"));
            titlePanel.Setters.Add(new Setter(Panel.MarginProperty, new Thickness(0, 0, 145, 0)));
            decorations.Children.Add(titlePanel);
            var title = new Style(selector => selector.Nesting().Template().OfType<TextBlock>());
            title.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            decorations.Children.Add(title);
            window.WindowDecorationsTheme = decorations;
        }
    }
}
