using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using static DiTunnel.App.Views.Dialogs;

namespace DiTunnel.App.Views;

internal static class SettingsPage
{
    public static async Task ShowAsync(ContentControl owner, MainViewModel vm)
    {
        var originalContent = owner.Content;
        IReadOnlyList<InstalledApplication> installedApplications = [];
        var applicationsLoaded = !(OperatingSystem.IsAndroid() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux());
        var splitDraft = new SplitTunnelRulesDraft(UserSettings.Current);
        var originalNetworkSettings = UserSettings.Current.NetworkSettingsFingerprint();
        var networkSettingsChangedExplicitly = false;
        var isClosing = false;
        Action? saveSplitRules = null;
        Action? refreshInstalledApplications = null;
        var expandedSections = new HashSet<string>(StringComparer.Ordinal);
        async Task GoBackAsync()
        {
            if (isClosing) return;
            isClosing = true;
            saveSplitRules?.Invoke();
            var networkSettingsChanged = networkSettingsChangedExplicitly || originalNetworkSettings != UserSettings.Current.NetworkSettingsFingerprint();
            owner.Content = originalContent;
            AppBackNavigation.Clear();
            if (TopLevel.GetTopLevel(owner) is Window window) window.Title = "Di-Tunnel";
            // Give Avalonia one frame to present the restored main view before a VPN
            // restart starts producing state changes. This avoids a transient empty frame.
            if (networkSettingsChanged)
            {
                await Task.Delay(50);
                await vm.ApplyNetworkSettingsAsync();
            }
        }
        void Build()
        {
            var sections = new StackPanel { Margin = new Thickness(24, 24, 24, 8), Spacing = 14, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
            var panel = sections;
            void BeginSection(string title)
            {
                panel = new StackPanel { Spacing = 14, Margin = new Thickness(0, 12, 0, 4) };
                var section = CreateSection(title, panel);
                section.IsExpanded = expandedSections.Contains(title);
                section.PropertyChanged += (_, change) =>
                {
                    if (change.Property != Expander.IsExpandedProperty) return;
                    if (section.IsExpanded) expandedSections.Add(title); else expandedSections.Remove(title);
                };
                sections.Children.Add(section);
            }
            var back = AsyncButton("← Назад", GoBackAsync);
            var status = Label("");
            CheckBox? killSwitchControl = null;
            void Save() { try { UserSettings.Current.Save(); status.Text = L.T("Настройки сохранены."); } catch { status.Text = L.T("Не удалось сохранить настройки."); } }
            BeginSection("Интерфейс");
            panel.Children.Add(Label("Язык"));
            var language = new ComboBox
            {
                ItemsSource = L.Languages.Select(item => item.Name).ToArray(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = L.Languages.ToList().FindIndex(item => item.Code == UserSettings.Current.Language)
            };
            language.SelectionChanged += (_, _) =>
            {
                if (language.SelectedIndex < 0 || L.Languages[language.SelectedIndex].Code == UserSettings.Current.Language) return;
                saveSplitRules?.Invoke();
                UserSettings.Current.Language = L.Languages[language.SelectedIndex].Code;
                // Updating Android's application locale recreates the Activity on API 33+.
                // The Avalonia UI has its own live translations, so refresh those in place.
                Save(); L.Apply(); vm.RefreshLanguage(); Build();
            };
            panel.Children.Add(language);
            var theme = new ComboBox { ItemsSource = new[] { L.T("Системная"), L.T("Тёмная"), L.T("Светлая") }, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Array.IndexOf(new[] { "system", "dark", "light" }, UserSettings.Current.Theme) };
            theme.SelectionChanged += (_, _) => { if (theme.SelectedIndex < 0) return; UserSettings.Current.Theme = new[] { "system", "dark", "light" }[theme.SelectedIndex]; UserSettings.Current.ApplyTheme(); Save(); };
            string[] closeActions = App.Platform.SupportsHideToTray ? ["ask", "hide", "exit"] : ["ask", "exit"];
            string[] closeLabels = App.Platform.SupportsHideToTray
                ? [L.T("Спрашивать каждый раз"), L.T("Скрыть в трей"), L.T("Выйти")]
                : [L.T("Спрашивать каждый раз"), L.T("Выйти")];
            var close = new ComboBox { ItemsSource = closeLabels, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = Math.Max(0, Array.IndexOf(closeActions, UserSettings.Current.CloseAction)) };
            close.SelectionChanged += (_, _) => { if (close.SelectedIndex < 0) return; UserSettings.Current.CloseAction = closeActions[close.SelectedIndex]; Save(); };
            var appearance = new Grid { ColumnDefinitions = new ColumnDefinitions(OperatingSystem.IsAndroid() ? "*" : "*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 6 };
            var themeLabel = Label("Тема");
            var closeLabel = Label("Действие при закрытии");
            appearance.Children.Add(themeLabel);
            if (!OperatingSystem.IsAndroid()) { appearance.Children.Add(closeLabel); Grid.SetColumn(closeLabel, 1); }
            appearance.Children.Add(theme); Grid.SetRow(theme, 1);
            if (!OperatingSystem.IsAndroid()) { appearance.Children.Add(close); Grid.SetColumn(close, 1); Grid.SetRow(close, 1); }
            panel.Children.Add(appearance);
            if (vm.Platform.ReleaseTarget == ReleaseTarget.WindowsX64)
            {
                var autostart = new CheckBox { Content = L.T("Запускать при входе в Windows"), IsChecked = UserSettings.Current.StartWithWindows };
                var minimized = new CheckBox { Content = L.T("Запускать в свёрнутом режиме"), IsChecked = UserSettings.Current.StartMinimized,
                    IsEnabled = UserSettings.Current.StartWithWindows };
                var changingAutostart = false;
                autostart.IsCheckedChanged += (_, _) =>
                {
                    if (changingAutostart) return;
                    var enabled = autostart.IsChecked == true;
                    try
                    {
                        if (App.SetWindowsAutostart is not { } register) throw new InvalidOperationException();
                        register(enabled);
                        UserSettings.Current.StartWithWindows = enabled;
                        minimized.IsEnabled = enabled;
                        Save();
                    }
                    catch
                    {
                        changingAutostart = true;
                        autostart.IsChecked = UserSettings.Current.StartWithWindows;
                        changingAutostart = false;
                        status.Text = L.T("Не удалось изменить автозапуск Windows.");
                    }
                };
                minimized.IsCheckedChanged += (_, _) => { UserSettings.Current.StartMinimized = minimized.IsChecked == true; Save(); };
                panel.Children.Add(autostart); panel.Children.Add(minimized);
            }
            BeginSection("Соединение");
            var ipv4Only = new CheckBox { Content = L.T("Использовать только IPv4"), IsChecked = UserSettings.Current.Ipv4Only };
            ipv4Only.IsCheckedChanged += (_, _) =>
            {
                networkSettingsChangedExplicitly = true;
                UserSettings.Current.Ipv4Only = ipv4Only.IsChecked == true;
                Save();
            };
            panel.Children.Add(ipv4Only);
            panel.Children.Add(Label("Отключает IPv6 внутри VPN и использует IPv4 для DNS. Изменение применяется при переподключении."));
            if (!vm.Platform.SupportsAdvancedNetworkSettings)
                panel.Children.Add(Label("На этом этапе Linux направляет весь трафик через VPN. Разделение трафика и блокировка рекламы пока недоступны."));
            var blockAds = new CheckBox { Content = L.T("Блокировать рекламу и трекеры"), IsChecked = vm.Platform.SupportsAdvancedNetworkSettings && UserSettings.Current.BlockAdsEnabled, IsEnabled = vm.Platform.SupportsAdvancedNetworkSettings };
            var strictAdBlocking = new CheckBox
            {
                Content = L.T("Строгая блокировка рекламы"),
                IsChecked = vm.Platform.SupportsAdvancedNetworkSettings && UserSettings.Current.StrictAdBlockingEnabled,
                IsEnabled = vm.Platform.SupportsAdvancedNetworkSettings && UserSettings.Current.BlockAdsEnabled
            };
            blockAds.IsCheckedChanged += (_, _) =>
            {
                networkSettingsChangedExplicitly = true;
                UserSettings.Current.BlockAdsEnabled = blockAds.IsChecked == true;
                strictAdBlocking.IsEnabled = vm.Platform.SupportsAdvancedNetworkSettings && UserSettings.Current.BlockAdsEnabled;
                Save();
            };
            panel.Children.Add(blockAds);
            panel.Children.Add(Label("Блокируются известные рекламные домены во всём трафике VPN. Реклама YouTube может не блокироваться, потому что доставляется с тех же доменов, что и видео."));
            strictAdBlocking.IsCheckedChanged += (_, _) =>
            {
                networkSettingsChangedExplicitly = true;
                UserSettings.Current.StrictAdBlockingEnabled = strictAdBlocking.IsChecked == true;
                Save();
            };
            panel.Children.Add(strictAdBlocking);
            panel.Children.Add(Label("Дополнительно блокируются общие домены, через которые мобильные рекламные SDK могут загружать объявления. Некоторые сервисы Яндекса могут перестать работать до отключения строгого режима и переподключения VPN."));
            var killSwitch = killSwitchControl = new CheckBox { Content = L.T("Kill switch: блокировать трафик вне VPN"), IsChecked = UserSettings.Current.KillSwitchEnabled, IsEnabled = vm.Platform.SupportsKillSwitch };
            var allowLan = new CheckBox { Content = L.T("Разрешать локальную сеть при активном kill switch"), IsChecked = UserSettings.Current.AllowLocalNetwork, IsEnabled = vm.Platform.SupportsKillSwitch && UserSettings.Current.KillSwitchEnabled };
            void SaveProtection()
            {
                networkSettingsChangedExplicitly = true;
                UserSettings.Current.KillSwitchEnabled = killSwitch.IsChecked == true;
                UserSettings.Current.AllowLocalNetwork = allowLan.IsChecked == true;
                allowLan.IsEnabled = vm.Platform.SupportsKillSwitch && UserSettings.Current.KillSwitchEnabled;
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
                panel.Children.Add(Label(vm.Platform.ReleaseTarget == ReleaseTarget.LinuxX64
                    ? vm.Platform.SupportsKillSwitch ? "При аварии VPN kill switch сохраняет блокировку до восстановления сети или явного отключения защиты." : "Kill switch и исключение для локальной сети пока недоступны на Linux."
                    : "Сетевые изменения применяются автоматически при возврате на главный экран. Kill switch использует отдельные правила Windows Filtering Platform."));
                if (vm.Platform.ReleaseTarget == ReleaseTarget.LinuxX64 && vm.Platform.SupportsKillSwitch)
                    panel.Children.Add(Label("Прямой трафик правил РТ разрешён при работающем VPN. После аварии он блокируется; настройка локальной сети остаётся в силе."));
            }
            var directSubscriptions = new CheckBox
            {
                Content = L.T("Обновлять подписки напрямую"),
                IsChecked = UserSettings.Current.RefreshSubscriptionsDirectly
            };
            directSubscriptions.IsCheckedChanged += (_, _) =>
            {
                UserSettings.Current.RefreshSubscriptionsDirectly = directSubscriptions.IsChecked == true;
                Save();
            };
            panel.Children.Add(directSubscriptions);
            BeginSection("Раздельное туннелирование");
            var splitMode = new ComboBox { ItemsSource = new[] { L.T("Всё через VPN"), L.T("Обход выбранных"), L.T("Только выбранные через VPN") }, SelectedIndex = vm.Platform.SupportsAdvancedNetworkSettings ? (int)UserSettings.Current.SplitTunnelMode : 0, IsEnabled = vm.Platform.SupportsAdvancedNetworkSettings };
            var activeMode = UserSettings.Current.SplitTunnelMode == SplitTunnelMode.ProxySelected ? SplitTunnelMode.ProxySelected : SplitTunnelMode.BypassSelected;
            var domains = new TextBox { Text = splitDraft.DomainText(activeMode), AcceptsReturn = true, MinHeight = 72, PlaceholderText = L.T(OperatingSystem.IsLinux() ? "Домены или IP-адреса, по одному в строке" : "Домены или IPv4-адреса, по одному в строке") };
            var splitRules = new StackPanel { Spacing = 12 };
            var selectedApplications = new HashSet<string>(splitDraft.ApplicationsFor(activeMode), StringComparer.Ordinal);
            SplitTunnelApplicationList? applicationList = null;
            splitRules.Children.Add(Label("Домены и IP-адреса"));
            splitRules.Children.Add(domains);
            if (OperatingSystem.IsAndroid() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                splitRules.Children.Add(Label(OperatingSystem.IsAndroid() ? "Приложения Android (можно выбрать несколько)" : OperatingSystem.IsLinux() ? "Приложения Linux (можно выбрать несколько)" : "Приложения Windows (можно выбрать несколько)"));
                if (OperatingSystem.IsLinux()) splitRules.Children.Add(Label("Для Snap/Flatpak и дочерних процессов добавьте фактический исполняемый файл, который использует сеть."));
                var search = new TextBox { PlaceholderText = L.T("Поиск приложений"), HorizontalAlignment = HorizontalAlignment.Stretch };
                splitRules.Children.Add(search);
                applicationList = new SplitTunnelApplicationList(selectedApplications, vm.Platform.ReleaseTarget);
                refreshInstalledApplications = () => applicationList.SetApplications(installedApplications);
                if (applicationsLoaded) refreshInstalledApplications();
                search.TextChanged += (_, _) => applicationList.Filter(search.Text);
                var autoSelectStatus = Label("");
                var autoSelect = Button("Автовыбор", () =>
                {
                    var before = selectedApplications.Count;
                    selectedApplications.UnionWith(ApplicationSelectionPresets.Select(activeMode, installedApplications));
                    applicationList.RefreshSelection();
                    autoSelectStatus.Text = $"{L.T("Автовыбор добавил приложений:")} {selectedApplications.Count - before}.";
                });
                autoSelect.HorizontalAlignment = HorizontalAlignment.Left;
                autoSelect.Margin = new Thickness(0, 0, 8, 4);
                var autoSelectRow = new WrapPanel();
                autoSelectRow.Children.Add(autoSelect);
                var clearSelection = Button("Снять все галочки", () =>
                {
                    selectedApplications.Clear();
                    applicationList.RefreshSelection();
                    autoSelectStatus.Text = L.T("Выбор приложений очищен.");
                });
                clearSelection.Margin = new Thickness(0, 0, 8, 4);
                autoSelectRow.Children.Add(clearSelection);
                splitRules.Children.Add(autoSelectRow);
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    var executableTitle = OperatingSystem.IsLinux() ? "Добавить исполняемый файл…" : "Добавить файл .exe…";
                    var addExecutable = AsyncButton(executableTitle, async () =>
                    {
                        var files = await TopLevel.GetTopLevel(owner)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = L.T(executableTitle), AllowMultiple = true,
                            FileTypeFilter = [new FilePickerFileType(OperatingSystem.IsLinux() ? "Linux" : "Windows") { Patterns = [OperatingSystem.IsLinux() ? "*" : "*.exe"] }]
                        });
                        var additions = files.Select(file => file.TryGetLocalPath()).OfType<string>()
                            .Where(path => File.Exists(path) && (OperatingSystem.IsLinux()
                                ? (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
                                : path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                            .Select(path => OperatingSystem.IsLinux() ? new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path : path)
                            .Select(path => new InstalledApplication(path, Path.GetFileNameWithoutExtension(path))).ToArray();
                        installedApplications = installedApplications.Concat(additions).DistinctBy(app => app.Id, vm.Platform.ApplicationPathComparer).ToArray();
                        selectedApplications.UnionWith(additions.Select(app => app.Id));
                        applicationList.SetApplications(installedApplications);
                    });
                    addExecutable.Margin = new Thickness(0, 0, 8, 4);
                    autoSelectRow.Children.Add(addExecutable);
                }
                splitRules.Children.Add(autoSelectStatus);
                splitRules.Children.Add(applicationList);
            }
            void CaptureDraft() => splitDraft.Capture(activeMode, domains.Text, selectedApplications);
            void UpdateSplitRulesVisibility() => splitRules.IsVisible = splitMode.SelectedIndex != (int)SplitTunnelMode.ProxyAll;
            splitMode.SelectionChanged += (_, _) =>
            {
                CaptureDraft();
                if (splitMode.SelectedIndex is (int)SplitTunnelMode.BypassSelected or (int)SplitTunnelMode.ProxySelected)
                {
                    activeMode = (SplitTunnelMode)splitMode.SelectedIndex;
                    domains.Text = splitDraft.DomainText(activeMode);
                    selectedApplications.Clear();
                    selectedApplications.UnionWith(splitDraft.ApplicationsFor(activeMode));
                    applicationList?.RefreshSelection();
                }
                UpdateSplitRulesVisibility();
            };
            UpdateSplitRulesVisibility();
            panel.Children.Add(splitMode); panel.Children.Add(splitRules);
            saveSplitRules = () =>
            {
                if (!vm.Platform.SupportsAdvancedNetworkSettings) return;
                CaptureDraft();
                splitDraft.ApplyTo(UserSettings.Current, (SplitTunnelMode)Math.Max(0, splitMode.SelectedIndex));
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
            var openLogs = Button("Открыть папку журналов", () =>
            {
                try { Directory.CreateDirectory(AppPaths.StateDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.StateDirectory) { UseShellExecute = true }); }
                catch { status.Text = L.T("Не удалось открыть папку."); }
            });
            openLogs.Margin = new Thickness(0, 0, 8, 6); serviceActions.Children.Add(openLogs);
            if (!OperatingSystem.IsAndroid())
            {
                BeginSection("Диагностика");
                panel.Children.Add(serviceActions);
                if (vm.CanRestoreNetwork)
                    panel.Children.Add(AsyncButton("Восстановить доступ к сети", async () =>
                    {
                        try
                        {
                            await vm.RestoreNetworkAsync();
                            if (killSwitchControl is not null) killSwitchControl.IsChecked = false;
                            status.Text = L.T("VPN и kill switch отключены. Доступ к сети восстановлен.");
                        }
                        catch { status.Text = L.T("Не удалось восстановить сеть. Повторите с правами администратора."); }
                    }));
                panel.Children.Add(Label("Экспорт содержит только события сети, без подписок и ключей."));
            }
            BeginSection("Обновления");
            var automaticUpdates = new CheckBox { Content = L.T("Автоматически скачивать и устанавливать обновления"), IsChecked = UserSettings.Current.InstallUpdatesAutomatically };
            automaticUpdates.IsCheckedChanged += (_, _) => { UserSettings.Current.InstallUpdatesAutomatically = automaticUpdates.IsChecked == true; Save(); };
            var updates = AsyncButton("Проверить обновления", async () =>
            {
                status.Text = L.T("Проверяем обновления…");
                status.Text = L.T(TopLevel.GetTopLevel(owner) is MainWindow window
                    ? await window.CheckForUpdatesAsync(true)
                    : await UpdateFlow.CheckAsync(owner, vm, true));
            });
            updates.Margin = new Thickness(0, 0, 8, 6);
            panel.Children.Add(automaticUpdates);
            panel.Children.Add(updates);
            panel.Children.Add(AsyncButton("Открыть проект на GitHub", async () =>
            {
                try { await (TopLevel.GetTopLevel(owner)?.Launcher?.LaunchUriAsync(new Uri("https://github.com/Divinty5/ditunnel")) ?? Task.FromResult(false)); }
                catch { status.Text = L.T("Не удалось открыть страницу релиза."); }
            }));
            sections.Children.Add(status);
            sections.Children.Add(Label($"Di-Tunnel · {UserSettings.Version}"));
            owner.Content = Page("Настройки", back, sections);
            if (TopLevel.GetTopLevel(owner) is Window window) window.Title = $"Di-Tunnel · {L.T("Настройки")}";
            AppBackNavigation.Set(() => _ = GoBackAsync());
        }
        Build();
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            try { installedApplications = await vm.GetInstalledApplicationsAsync(); }
            catch { installedApplications = []; }
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                installedApplications = installedApplications.Concat(splitDraft.Applications
                    .Where(File.Exists).Select(path => new InstalledApplication(path, Path.GetFileNameWithoutExtension(path))))
                    .DistinctBy(app => app.Id, vm.Platform.ApplicationPathComparer).ToArray();
            applicationsLoaded = true;
            if (!isClosing)
            {
                // Keep the page, focus, draft edits and outer ScrollViewer intact.
                refreshInstalledApplications?.Invoke();
            }
        }
    }

    internal static Expander CreateSection(string title, Control content)
    {
        var heading = Label(title);
        heading.FontWeight = FontWeight.Bold;
        heading.FontSize = 17;
        heading.Bind(TextBlock.ForegroundProperty, heading.GetResourceObservable("AccentTextBrush"));
        return new Expander
        {
            Classes = { "settings-section" },
            Header = heading, Content = content, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }
}
