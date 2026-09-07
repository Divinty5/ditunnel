using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiTunnel.Core.Connection;
using DiTunnel.App.Controls;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App.Views;

public static class Dialogs
{
    public static Button Button(string text, Action action)
    {
        var button = new Button { Content = L.T(text), Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(10) };
        button.Click += (_, _) => action();
        return button;
    }
    private static TextBlock Label(string text) => new() { Text = L.T(text), TextWrapping = TextWrapping.Wrap };
    private static ScrollViewer Scroll(Control content)
    {
        var scroll = new ScrollViewer { Content = content };
        scroll.Bind(ScrollViewer.BackgroundProperty, scroll.GetResourceObservable("PageBrush"));
        return scroll;
    }
    private static Window Window(Window owner, string title, double height = 560) => new()
    {
        Title = L.T(title), Width = Math.Min(560, owner.Width), Height = Math.Min(height, owner.Height),
        MinWidth = 320, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Icon = owner.Icon
    };

    public static async Task Settings(Window owner)
    {
        var originalContent = owner.Content;
        void Build()
        {
            owner.Title = $"Di-Tunnel · {L.T("Настройки")}";
            var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            Action? saveSplitRules = null;
            panel.Children.Add(Button("← Назад", () => { saveSplitRules?.Invoke(); owner.Content = originalContent; owner.Title = "Di-Tunnel"; }));
            panel.Children.Add(new TextBlock { Text = L.T("Настройки"), FontSize = 24 });
            var status = Label("");
            void Save() { try { UserSettings.Current.Save(); status.Text = L.T("Настройки сохранены."); } catch { status.Text = L.T("Не удалось сохранить настройки."); } }
            panel.Children.Add(Button("Язык: Русский → English", () =>
            {
                saveSplitRules?.Invoke();
                UserSettings.Current.Language = UserSettings.Current.Language == "ru" ? "en" : "ru";
                L.Apply(); Save(); Build();
            }));
            var theme = new ComboBox { ItemsSource = new[] { L.T("Системная"), L.T("Тёмная"), L.T("Светлая") }, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Array.IndexOf(new[] { "system", "dark", "light" }, UserSettings.Current.Theme) };
            theme.SelectionChanged += (_, _) => { if (theme.SelectedIndex < 0) return; UserSettings.Current.Theme = new[] { "system", "dark", "light" }[theme.SelectedIndex]; UserSettings.Current.ApplyTheme(); Save(); };
            var close = new ComboBox { ItemsSource = new[] { L.T("Спрашивать каждый раз"), L.T("Скрыть в трей"), L.T("Выйти") }, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Array.IndexOf(new[] { "ask", "hide", "exit" }, UserSettings.Current.CloseAction) };
            close.SelectionChanged += (_, _) => { if (close.SelectedIndex < 0) return; UserSettings.Current.CloseAction = new[] { "ask", "hide", "exit" }[close.SelectedIndex]; Save(); };
            var appearance = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
            var themeField = new StackPanel { Spacing = 6 }; themeField.Children.Add(Label("Тема")); themeField.Children.Add(theme);
            var closeField = new StackPanel { Spacing = 6 }; closeField.Children.Add(Label("Действие при закрытии")); closeField.Children.Add(close);
            appearance.Children.Add(themeField); appearance.Children.Add(closeField); Grid.SetColumn(closeField, 1); panel.Children.Add(appearance);
            panel.Children.Add(Label("Раздельное туннелирование"));
            var splitMode = new ComboBox { ItemsSource = new[] { L.T("Всё через VPN"), L.T("Обход выбранных"), L.T("Только выбранные через VPN") }, SelectedIndex = (int)UserSettings.Current.SplitTunnelMode };
            var domains = new TextBox { Text = string.Join(Environment.NewLine, UserSettings.Current.SplitTunnelDomains), AcceptsReturn = true, MinHeight = 72, PlaceholderText = L.T("Домены, по одному в строке") };
            var processes = new TextBox { Text = string.Join(Environment.NewLine, UserSettings.Current.SplitTunnelProcesses), AcceptsReturn = true, MinHeight = 72, PlaceholderText = L.T("Приложения: имя процесса или путь к .exe, по одному в строке") };
            panel.Children.Add(splitMode); panel.Children.Add(domains); panel.Children.Add(processes);
            saveSplitRules = () =>
            {
                UserSettings.Current.SplitTunnelMode = (DiTunnel.Core.Connection.SplitTunnelMode)Math.Max(0, splitMode.SelectedIndex);
                UserSettings.Current.SplitTunnelDomains = domains.Text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => !string.IsNullOrWhiteSpace(domain)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                UserSettings.Current.SplitTunnelProcesses = processes.Text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                try { UserSettings.Current.Save(); status.Text = L.T("Правила будут применены при следующем подключении VPN."); } catch { status.Text = L.T("Не удалось сохранить настройки."); }
            };
            var serviceActions = new WrapPanel();
            var export = Button("Выгрузить журналы…", async () =>
            {
                try
                {
                    var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = L.T("Выгрузить журналы…"), SuggestedFileName = $"DiTunnel-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip", DefaultExtension = "zip" });
                    if (file?.TryGetLocalPath() is not { } path) return;
                    // Build separately so overwriting an existing export is atomic and never appends to it.
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { Diagnostics.Export(temp); File.Move(temp, path, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    status.Text = L.T("Журналы сохранены.");
                }
                catch { status.Text = L.T("Не удалось выгрузить журналы."); }
            });
            export.Margin = new Thickness(0, 0, 8, 6); serviceActions.Children.Add(export);
            panel.Children.Add(Label("Экспорт содержит только события сети, без подписок и ключей."));
            var openLogs = Button("Открыть папку журналов", () =>
            {
                try { Directory.CreateDirectory(UserSettings.DataDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UserSettings.DataDirectory) { UseShellExecute = true }); }
                catch { status.Text = L.T("Не удалось открыть папку."); }
            });
            openLogs.Margin = new Thickness(0, 0, 8, 6); serviceActions.Children.Add(openLogs);
            var updates = Button("Проверить обновления", async () =>
            {
                status.Text = L.T("Проверяем…");
                var result = await ReleaseChecker.CheckAsync();
                status.Text = L.T(result.Message);
                if (result.Url is { } url)
                {
                    var open = Button("Открыть релиз на GitHub", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }));
                    panel.Children.Add(open);
                }
            });
            serviceActions.Children.Add(updates);
            panel.Children.Add(serviceActions);
            panel.Children.Add(Label($"Di-Tunnel · {UserSettings.Version}"));
            panel.Children.Add(status);
            owner.Content = Scroll(panel);
        }
        Build();
        await Task.CompletedTask;
    }

    public static async Task<string?> AskClose(Window owner)
    {
        var dialog = Window(owner, "Закрыть Di-Tunnel?", 260);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(Label("Скрыть приложение и оставить VPN работающим или выйти и отключить VPN?"));
        panel.Children.Add(Button("Скрыть в трей", () => dialog.Close("hide")));
        panel.Children.Add(Button("Выйти", () => dialog.Close("exit")));
        panel.Children.Add(Button("Отмена", () => dialog.Close((string?)null)));
        dialog.Content = Scroll(panel);
        return await dialog.ShowDialog<string?>(owner);
    }

    public static async Task Subscriptions(Window owner, MainViewModel vm)
    {
        var originalContent = owner.Content;
        owner.Title = $"Di-Tunnel · {L.T("Подписки и профили")}";
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(Button("← Назад", () => { owner.Content = originalContent; owner.Title = "Di-Tunnel"; }));
        panel.Children.Add(new TextBlock { Text = L.T("Подписки и профили"), FontSize = 24 });
        var groups = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding("Groups"));
        groups.Bind(ComboBox.SelectedItemProperty, new Binding("SelectedGroup") { Mode = BindingMode.TwoWay });
        groups.Bind(Control.IsEnabledProperty, new Binding("CanSelectProfile"));
        panel.Children.Add(groups);
        var list = new ListBox
        {
            MinHeight = 180, MaxHeight = 480, Background = Brushes.Transparent,
            ItemsPanel = new FuncTemplate<Panel?>(() => new AdaptiveTilePanel()),
            ItemTemplate = new FuncDataTemplate<ServerItemViewModel>((row, _) =>
            {
                var flag = new Image { Width = 34, Height = 26, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                flag.Bind(Image.SourceProperty, new Binding(nameof(ServerItemViewModel.FlagImage)));
                flag.Bind(Visual.IsVisibleProperty, new Binding(nameof(ServerItemViewModel.HasFlag)));
                var globe = new TextBlock { Text = "🌐", FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                globe.Bind(Visual.IsVisibleProperty, new Binding("!" + nameof(ServerItemViewModel.HasFlag)));
                var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Name)));
                var summary = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
                summary.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Protocol)) { Converter = new TranslationConverter() });
                var latency = new TextBlock { FontSize = 11, Foreground = Brushes.MediumPurple, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 155 };
                latency.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.ProbeText)) { Converter = new TranslationConverter() });
                var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
                details.Children.Add(name); details.Children.Add(summary); Grid.SetRow(summary, 1);
                var rowPanel = new Grid { MinHeight = 58, Margin = new Thickness(10, 7), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                rowPanel.Children.Add(flag); rowPanel.Children.Add(globe); rowPanel.Children.Add(details); rowPanel.Children.Add(latency);
                Grid.SetColumn(details, 1); Grid.SetColumn(latency, 2);
                return rowPanel;
            })
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding("Profiles"));
        list.Bind(ListBox.SelectedItemProperty, new Binding("SelectedProfile") { Mode = BindingMode.TwoWay });
        list.Bind(Control.IsEnabledProperty, new Binding("CanSelectProfile"));
        panel.Children.Add(list);
        var testActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        var testOne = Button("Проверить сервер", () => vm.ProbeSelectedCommand.Execute(null));
        testOne.Margin = new Thickness(0, 0, 8, 4); testOne.Bind(Control.IsEnabledProperty, new Binding("CanProbe")); testActions.Children.Add(testOne);
        var testAll = Button("Проверить все", () => vm.ProbeAllCommand.Execute(null));
        testAll.Margin = new Thickness(0, 0, 8, 4); testAll.Bind(Control.IsEnabledProperty, new Binding("CanProbeAll")); testActions.Children.Add(testAll);
        var cancelTests = Button("Отменить проверку", () => vm.CancelProbesCommand.Execute(null));
        cancelTests.Bind(Visual.IsVisibleProperty, new Binding("IsProbing")); testActions.Children.Add(cancelTests);
        panel.Children.Add(testActions);
        var lowest = new CheckBox { Content = L.T("Lowest: выбирать сервер с минимальной задержкой"), IsChecked = vm.IsLowestMode };
        lowest.IsCheckedChanged += (_, _) => vm.SetLowestMode(lowest.IsChecked == true);
        panel.Children.Add(lowest);
        panel.Children.Add(Label("Проверки: через 5, затем 10, затем каждые 15 минут. При смене сервера VPN переподключится."));
        var management = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refresh = Button("Обновить подписки", async () => await vm.RefreshSubscriptionsAsync());
        refresh.Margin = new Thickness(0, 0, 8, 6); refresh.Bind(Control.IsEnabledProperty, new Binding("CanImport")); management.Children.Add(refresh);
        var remove = Button("Удалить всю подписку", async () =>
        {
            var confirmation = Window(owner, "Удалить профиль?", 240);
            var stack = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
            stack.Children.Add(Label("Все серверы выбранного профиля будут удалены."));
            stack.Children.Add(Button("Удалить", () => confirmation.Close(true)));
            stack.Children.Add(Button("Отмена", () => confirmation.Close(false)));
            confirmation.Content = stack;
            if (await confirmation.ShowDialog<bool>(owner)) vm.RemoveGroupCommand.Execute(null);
        });
        remove.Margin = new Thickness(0, 0, 8, 6); remove.Bind(Control.IsEnabledProperty, new Binding("CanImport")); management.Children.Add(remove);
        var removeOne = Button("Удалить выбранный", () => vm.RemoveProfileCommand.Execute(null));
        removeOne.Margin = new Thickness(0, 0, 8, 6); removeOne.Bind(Control.IsEnabledProperty, new Binding("CanImport")); management.Children.Add(removeOne);
        panel.Children.Add(management);
        var status = Label(""); status.Bind(TextBlock.TextProperty, new Binding("SubscriptionStatus") { Converter = new TranslationConverter() }); panel.Children.Add(status);
        panel.Children.Add(Label("При выборе другого сервера активный VPN переподключится автоматически."));
        owner.Content = Scroll(panel);
        await Task.CompletedTask;
    }
}
