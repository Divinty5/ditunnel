using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
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
    private static Button AsyncButton(string text, Func<Task> action)
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
    private static TextBlock Label(string text) => new() { Text = L.T(text), TextWrapping = TextWrapping.Wrap };
    private static ScrollViewer Scroll(Control content)
    {
        var scroll = new ScrollViewer { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        scroll.Bind(ScrollViewer.BackgroundProperty, scroll.GetResourceObservable("PageBrush"));
        return scroll;
    }
    private static Control Page(string title, Button back, Control content)
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
        var scroll = Scroll(content); root.Children.Add(scroll); Grid.SetRow(scroll, 1);
        return root;
    }
    private static Window Window(Window owner, string title, double height = 560) => new()
    {
        Title = L.T(title), Width = Math.Min(560, owner.Width), Height = Math.Min(height, owner.Height),
        MinWidth = 320, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Icon = owner.Icon
    };

    public static async Task Settings(ContentControl owner, MainViewModel vm)
    {
        var originalContent = owner.Content;
        var installedApplications = OperatingSystem.IsAndroid()
            ? await vm.GetInstalledApplicationsAsync()
            : [];
        var originalNetworkSettings = NetworkSettingsFingerprint();
        var networkSettingsChangedExplicitly = false;
        void Build()
        {
            if (owner is Window window) window.Title = $"Di-Tunnel · {L.T("Настройки")}";
            var panel = new StackPanel { Margin = new Thickness(24, 24, 24, 8), Spacing = 14, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center };
            Action? saveSplitRules = null;
            var back = AsyncButton("← Назад", async () =>
            {
                saveSplitRules?.Invoke();
                var networkSettingsChanged = networkSettingsChangedExplicitly || originalNetworkSettings != NetworkSettingsFingerprint();
                owner.Content = originalContent;
                if (owner is Window window) window.Title = "Di-Tunnel";
                if (networkSettingsChanged)
                    await vm.ApplyNetworkSettingsAsync();
            });
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
            var appearance = new Grid { ColumnDefinitions = new ColumnDefinitions(OperatingSystem.IsAndroid() ? "*" : "*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 6 };
            var themeLabel = Label("Тема");
            var closeLabel = Label("Действие при закрытии");
            appearance.Children.Add(themeLabel);
            if (!OperatingSystem.IsAndroid()) { appearance.Children.Add(closeLabel); Grid.SetColumn(closeLabel, 1); }
            appearance.Children.Add(theme); Grid.SetRow(theme, 1);
            if (!OperatingSystem.IsAndroid()) { appearance.Children.Add(close); Grid.SetColumn(close, 1); Grid.SetRow(close, 1); }
            panel.Children.Add(appearance);
            panel.Children.Add(Label("Защита соединения"));
            var killSwitch = new CheckBox { Content = L.T("Kill switch: блокировать трафик вне VPN"), IsChecked = UserSettings.Current.KillSwitchEnabled };
            var allowLan = new CheckBox { Content = L.T("Разрешать локальную сеть при активном kill switch"), IsChecked = UserSettings.Current.AllowLocalNetwork, IsEnabled = UserSettings.Current.KillSwitchEnabled };
            void SaveProtection()
            {
                networkSettingsChangedExplicitly = true;
                UserSettings.Current.KillSwitchEnabled = killSwitch.IsChecked == true;
                UserSettings.Current.AllowLocalNetwork = allowLan.IsChecked == true;
                allowLan.IsEnabled = UserSettings.Current.KillSwitchEnabled;
                Save();
                vm.RefreshConnectionPolicy();
            }
            killSwitch.IsCheckedChanged += (_, _) => SaveProtection();
            allowLan.IsCheckedChanged += (_, _) => SaveProtection();
            if (OperatingSystem.IsAndroid())
            {
                panel.Children.Add(Label("Для полной блокировки трафика вне VPN используйте системные настройки Android: постоянный VPN и блокировку подключений без VPN. Встроенный kill switch Windows на Android недоступен."));
            }
            else
            {
                panel.Children.Add(killSwitch);
                panel.Children.Add(allowLan);
                panel.Children.Add(Label("Сетевые изменения применяются автоматически при возврате на главный экран. Kill switch использует отдельные правила Windows Filtering Platform."));
            }
            panel.Children.Add(Label("Раздельное туннелирование"));
            var splitMode = new ComboBox { ItemsSource = new[] { L.T("Всё через VPN"), L.T("Обход выбранных"), L.T("Только выбранные через VPN") }, SelectedIndex = (int)UserSettings.Current.SplitTunnelMode };
            var domains = new TextBox { Text = string.Join(Environment.NewLine, UserSettings.Current.SplitTunnelDomains), AcceptsReturn = true, MinHeight = 72, PlaceholderText = L.T("Домены или IPv4-адреса, по одному в строке") };
            var splitRules = new StackPanel { Spacing = 12 };
            var selectedApplications = UserSettings.Current.SplitTunnelProcesses.ToHashSet(StringComparer.Ordinal);
            if (OperatingSystem.IsAndroid())
            {
                splitRules.Children.Add(Label("Приложения Android (можно выбрать несколько)"));
                var applicationRows = new StackPanel { Spacing = 2 };
                foreach (var application in installedApplications)
                {
                    var check = new CheckBox
                    {
                        Content = $"{application.Name}\n{application.Id}",
                        IsChecked = selectedApplications.Contains(application.Id),
                        Padding = new Thickness(8, 6)
                    };
                    check.IsCheckedChanged += (_, _) =>
                    {
                        if (check.IsChecked == true) selectedApplications.Add(application.Id);
                        else selectedApplications.Remove(application.Id);
                    };
                    applicationRows.Children.Add(check);
                }
                splitRules.Children.Add(new ScrollViewer { Content = applicationRows, MinHeight = 140, MaxHeight = 320 });
            }
            splitRules.Children.Add(Label("Домены и IP-адреса"));
            splitRules.Children.Add(domains);
            void UpdateSplitRulesVisibility() => splitRules.IsVisible = splitMode.SelectedIndex != (int)SplitTunnelMode.ProxyAll;
            splitMode.SelectionChanged += (_, _) => UpdateSplitRulesVisibility();
            UpdateSplitRulesVisibility();
            panel.Children.Add(splitMode); panel.Children.Add(splitRules);
            saveSplitRules = () =>
            {
                UserSettings.Current.SplitTunnelMode = (DiTunnel.Core.Connection.SplitTunnelMode)Math.Max(0, splitMode.SelectedIndex);
                UserSettings.Current.SplitTunnelDomains = domains.Text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => !string.IsNullOrWhiteSpace(domain)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                if (OperatingSystem.IsAndroid())
                    UserSettings.Current.SplitTunnelProcesses = selectedApplications.OrderBy(value => value, StringComparer.Ordinal).ToList();
                try { UserSettings.Current.Save(); status.Text = L.T("Правила будут применены при следующем подключении VPN."); } catch { status.Text = L.T("Не удалось сохранить настройки."); }
            };
            var serviceActions = new WrapPanel { IsVisible = !OperatingSystem.IsAndroid() };
            var export = Button("Выгрузить журналы…", async () =>
            {
                try
                {
                    var file = await TopLevel.GetTopLevel(owner)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = L.T("Выгрузить журналы…"), SuggestedFileName = $"DiTunnel-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip", DefaultExtension = "zip" });
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
            if (!OperatingSystem.IsAndroid()) panel.Children.Add(Label("Экспорт содержит только события сети, без подписок и ключей."));
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
            updates.Margin = new Thickness(0, 0, 8, 6);
            serviceActions.Children.Add(updates);
            panel.Children.Add(serviceActions);
            panel.Children.Add(status);
            panel.Children.Add(Label($"Di-Tunnel · {UserSettings.Version}"));
            owner.Content = Page("Настройки", back, panel);
        }
        Build();
        await Task.CompletedTask;
    }

    private static string NetworkSettingsFingerprint()
    {
        static string Canonical(IEnumerable<string> values) => string.Join('\n', values
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));

        var settings = UserSettings.Current;
        return settings.SplitTunnelMode == SplitTunnelMode.ProxyAll
            ? string.Join('|', settings.KillSwitchEnabled, settings.AllowLocalNetwork, (int)settings.SplitTunnelMode)
            : string.Join('|', settings.KillSwitchEnabled, settings.AllowLocalNetwork,
                (int)settings.SplitTunnelMode, Canonical(settings.SplitTunnelDomains), Canonical(settings.SplitTunnelProcesses));
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

    public static async Task Subscriptions(ContentControl owner, MainViewModel vm)
    {
        var originalContent = owner.Content;
        if (owner is Window window) window.Title = $"Di-Tunnel · {L.T("Подписки и профили")}";
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Center };
        var back = Button("← Назад", () => { owner.Content = originalContent; if (owner is Window window) window.Title = "Di-Tunnel"; });
        var groups = new ComboBox { MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding("Groups"));
        groups.Bind(ComboBox.SelectedItemProperty, new Binding("SelectedGroup") { Mode = BindingMode.TwoWay });
        groups.Bind(Control.IsEnabledProperty, new Binding("CanSelectProfile"));
        panel.Children.Add(groups);
        var list = new ListBox
        {
            MinHeight = 140, MaxHeight = 420, Background = Brushes.Transparent,
            ItemsPanel = new FuncTemplate<Panel?>(() => new AdaptiveTilePanel { MinimumTileWidth = 200, MaximumTileWidth = 360, TileSpacing = 6 }),
            ItemTemplate = new FuncDataTemplate<ServerItemViewModel>((row, _) =>
            {
                var flag = new Image { Width = 30, Height = 22, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                flag.Bind(Image.SourceProperty, new Binding(nameof(ServerItemViewModel.FlagImage)));
                flag.Bind(Visual.IsVisibleProperty, new Binding(nameof(ServerItemViewModel.HasFlag)));
                var globe = new TextBlock { Text = "🌐", FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                globe.Bind(Visual.IsVisibleProperty, new Binding("!" + nameof(ServerItemViewModel.HasFlag)));
                var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Name)));
                var summary = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
                summary.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Protocol)) { Converter = new TranslationConverter() });
                var latency = new TextBlock { FontSize = 11, Foreground = Brushes.MediumPurple, TextTrimming = TextTrimming.CharacterEllipsis };
                latency.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.ProbeText)) { Converter = new TranslationConverter() });
                var details = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), MinWidth = 130 };
                details.Children.Add(name); details.Children.Add(summary); details.Children.Add(latency);
                Grid.SetRow(summary, 1); Grid.SetRow(latency, 2);
                var rowPanel = new Grid { MinHeight = 56, Margin = new Thickness(8, 4), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                rowPanel.Children.Add(flag); rowPanel.Children.Add(globe); rowPanel.Children.Add(details);
                Grid.SetColumn(details, 1);
                return rowPanel;
            })
        };
        list.Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(Layoutable.MinHeightProperty, 0d),
                new Avalonia.Styling.Setter(Layoutable.MaxHeightProperty, 72d)
            }
        });
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
        var confirmationText = Label("Удалить выбранную подписку и все её серверы?");
        var confirmationActions = new WrapPanel();
        var confirmationCard = new Border
        {
            Width = 300, MaxWidth = 300, Padding = new Thickness(18), CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Spacing = 12, Children = { confirmationText, confirmationActions } }
        };
        confirmationCard.Bind(Border.BackgroundProperty, confirmationCard.GetResourceObservable("CardBrush"));
        var confirmation = new Border { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Child = confirmationCard, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var confirmDelete = Button("Удалить", () => { confirmation.IsVisible = false; vm.RemoveGroupCommand.Execute(null); });
        confirmDelete.Margin = new Thickness(0, 0, 8, 0); confirmationActions.Children.Add(confirmDelete);
        confirmationActions.Children.Add(Button("Отмена", () => confirmation.IsVisible = false));
        var management = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refresh = Button("Обновить подписки", async () => await vm.RefreshSubscriptionsAsync());
        refresh.Margin = new Thickness(0, 0, 8, 6); refresh.Bind(Control.IsEnabledProperty, new Binding("CanRefreshSubscriptions")); management.Children.Add(refresh);
        var remove = Button("Удалить всю подписку", () => confirmation.IsVisible = true);
        remove.Margin = new Thickness(0, 0, 8, 6); remove.Bind(Control.IsEnabledProperty, new Binding("CanManageProfiles")); management.Children.Add(remove);
        var removeOne = Button("Удалить выбранный", () => vm.RemoveProfileCommand.Execute(null));
        removeOne.Margin = new Thickness(0, 0, 8, 6); removeOne.Bind(Control.IsEnabledProperty, new Binding("CanManageProfiles")); management.Children.Add(removeOne);
        panel.Children.Add(management);
        var status = Label(""); status.Bind(TextBlock.TextProperty, new Binding("SubscriptionStatus") { Converter = new TranslationConverter() }); panel.Children.Add(status);
        panel.Children.Add(Label("При выборе другого сервера активный VPN переподключится автоматически."));
        var page = Page("Подписки и профили", back, panel);
        var root = new Grid();
        root.Children.Add(page);
        root.Children.Add(confirmation);
        owner.Content = root;
        await Task.CompletedTask;
    }
}
