using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DiTunnel.Core.Connection;
using DiTunnel.App.Controls;
using DiTunnel.App.ViewModels;
using static DiTunnel.App.Views.Dialogs;

namespace DiTunnel.App.Views;

internal static class SubscriptionsPage
{
    public static void Show(ContentControl owner, MainViewModel vm, Action? closePage = null)
    {
        var originalContent = owner.Content;
        if (TopLevel.GetTopLevel(owner) is Window window) window.Title = $"Di-Tunnel · {L.T("Подписки и профили")}";
        var mobile = vm.Platform.ReleaseTarget == ReleaseTarget.AndroidArm64;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14, MaxWidth = 1100,
            HorizontalAlignment = mobile ? HorizontalAlignment.Stretch : HorizontalAlignment.Center };
        void GoBack()
        {
            if (closePage is not null) closePage();
            else { owner.Content = originalContent; AppBackNavigation.Clear(); }
            if (TopLevel.GetTopLevel(owner) is Window window) window.Title = "Di-Tunnel";
        }
        var back = Button("← Назад", GoBack);
        AppBackNavigation.Set(GoBack);
        var groups = new ComboBox { MaxWidth = mobile ? double.PositiveInfinity : 360, HorizontalAlignment = HorizontalAlignment.Stretch };
        groups.Bind(ItemsControl.ItemsSourceProperty, new Binding("Groups"));
        groups.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedGroup)) { Mode = BindingMode.OneWay });
        groups.SelectionChanged += (_, _) =>
        {
            if (vm.CanSelectProfile && groups.SelectedItem is ProfileGroup selected) vm.SelectedGroup = selected;
        };
        groups.Bind(Control.IsEnabledProperty, new Binding("CanSelectProfile"));
        panel.Children.Add(groups);
        var list = new ListBox
        {
            MinHeight = mobile ? 84 : 140, MaxHeight = AdaptiveTilePanel.MaximumViewportHeight, Background = Brushes.Transparent,
            ItemsPanel = new FuncTemplate<Panel?>(() => new AdaptiveTilePanel(mobile)),
            ItemTemplate = new FuncDataTemplate<ServerItemViewModel>((row, _) =>
            {
                var flag = new Image { Width = 60, Height = 42, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                flag.Bind(Image.SourceProperty, new Binding(nameof(ServerItemViewModel.FlagImage)));
                flag.Bind(Visual.IsVisibleProperty, new Binding(nameof(ServerItemViewModel.HasFlag)));
                var globe = new TextBlock { Text = "🌐", IsVisible = false, FontSize = 38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
                globe.Bind(Visual.IsVisibleProperty, new Binding("!" + nameof(ServerItemViewModel.HasFlag)));
                var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Name)));
                var summary = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
                summary.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.Protocol)) { Converter = new TranslationConverter() });
                var latency = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                latency.Bind(TextBlock.TextProperty, new Binding(nameof(ServerItemViewModel.ProbeDisplayText)) { Converter = new TranslationConverter() });
                latency.Bind(ToolTip.TipProperty, new Binding(nameof(ServerItemViewModel.ProbeStatusText)) { Converter = new TranslationConverter() });
                latency.Bind(TextBlock.ForegroundProperty, new MultiBinding
                {
                    Converter = LatencyThemeConverter.Instance,
                    Bindings = { new Binding(nameof(ServerItemViewModel.ProbeBrush)), new Binding(nameof(StyledElement.ActualThemeVariant)) { Source = latency } }
                });
                var details = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), MinWidth = 130,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var latencyBadge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3),
                    HorizontalAlignment = HorizontalAlignment.Left, Child = latency, Margin = new Thickness(0, 3, 0, 0) };
                latencyBadge.Bind(Border.BackgroundProperty, latencyBadge.GetResourceObservable("LatencyBadgeBrush"));
                details.Children.Add(name); details.Children.Add(summary); details.Children.Add(latencyBadge);
                Grid.SetRow(summary, 1); Grid.SetRow(latencyBadge, 2);
                var rowPanel = new Grid { MinHeight = 76, Margin = new Thickness(8, 4), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                rowPanel.Children.Add(flag); rowPanel.Children.Add(globe); rowPanel.Children.Add(details);
                Grid.SetColumn(details, 1);
                var tile = new Border { Background = Brushes.Transparent, Child = rowPanel };
                tile.Bind(Border.BackgroundProperty, new Binding(nameof(ServerItemViewModel.ActiveConnectionBrush))
                {
                    TargetNullValue = Brushes.Transparent
                });
                return tile;
            })
        };
        list.Styles.Add(new Avalonia.Styling.Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Avalonia.Styling.Setter(Layoutable.MinHeightProperty, 0d),
                new Avalonia.Styling.Setter(Layoutable.MaxHeightProperty, AdaptiveTilePanel.TileHeight),
                new Avalonia.Styling.Setter(InputElement.IsHoldWithMouseEnabledProperty, true),
                new Avalonia.Styling.Setter(Control.IsEnabledProperty, new Binding("CanSelectProfile") { Source = vm })
            }
        });
        // Avalonia checks the original hit target, which can be text, a flag or a badge.
        list.Styles.Add(new Style(selector => selector.Is<ListBoxItem>().Descendant().Is<InputElement>())
        {
            Setters = { new Setter(InputElement.IsHoldWithMouseEnabledProperty, true) }
        });
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding("Profiles"));
        list.Bind(Visual.IsVisibleProperty, new Binding("!" + nameof(MainViewModel.IsSelectedSubscriptionEmpty)));
        // Keep the ListBox and its ScrollViewer enabled while probes run. Only
        // selecting a row is blocked; mouse wheel and touch scrolling stay active.
        list.Bind(ListBox.SelectedItemProperty, new Binding("SelectedProfile") { Mode = BindingMode.OneWay });
        // ListBox owns the touch gesture on Android, so child Tapped handlers are not
        // reliable. SelectionChanged is raised after the gesture has selected the row.
        list.SelectionChanged += (_, _) =>
        {
            if (vm.CanSelectProfile && list.SelectedItem is ServerItemViewModel selected) vm.SelectedProfile = selected;
            else if (list.SelectedItem is not null && vm.SelectedProfile is { } current &&
                     !ReferenceEquals(list.SelectedItem, current) && vm.Profiles.Contains(current))
                list.SelectedItem = current;
        };
        ServerItemViewModel? ProfileAt(object? source) => source is Visual visual
            ? visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ServerItemViewModel : null;
        ServerItemViewModel? mouseHoldRow = null;
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Mouse)
                mouseHoldRow = e.GetCurrentPoint(list).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
                    ? ProfileAt(e.Source) : null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        var touchTaps = OperatingSystem.IsAndroid() ? new ProfileTouchTaps() : null;
        if (touchTaps is not null)
        {
            // Match the row across taps. A flag, title and latency badge can each be
            // a different pointer target within the same row.
            list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (e.Pointer.Type == PointerType.Touch)
                    touchTaps.Press(e.Pointer.IsPrimary ? ProfileAt(e.Source) : null,
                        e.Pointer.Id, e.GetPosition(list), e.Timestamp);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (e.Pointer.Type == PointerType.Touch)
                    touchTaps.Move(e.Pointer.Id, e.GetPosition(list),
                        list.GetPlatformSettings()?.GetTapSize(PointerType.Touch) ?? new Size(8, 8));
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(InputElement.PointerReleasedEvent, async (_, e) =>
            {
                if (e.Pointer.Type != PointerType.Touch) return;
                var settings = list.GetPlatformSettings();
                var selected = touchTaps.Release(ProfileAt(e.Source), e.Pointer.Id, e.GetPosition(list), e.Timestamp,
                    settings?.GetTapSize(PointerType.Touch) ?? new Size(8, 8),
                    settings?.GetDoubleTapSize(PointerType.Touch) ?? new Size(24, 24),
                    settings?.GetDoubleTapTime(PointerType.Touch) ?? TimeSpan.FromMilliseconds(300));
                if (selected is not null) await vm.ActivateProfileFromUserAsync(selected);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        list.DoubleTapped += async (_, e) =>
        {
            if (touchTaps is not null && e.Pointer.Type == PointerType.Touch) return;
            if (ProfileAt(e.Source) is not { } selected) return;
            e.Handled = true;
            await vm.ActivateProfileFromUserAsync(selected);
        };
        list.Holding += async (_, e) =>
        {
            if (e.HoldingState != HoldingState.Started || ProfileAt(e.Source) is not { } selected) return;
            if (e.PointerType == PointerType.Mouse && !ReferenceEquals(mouseHoldRow, selected)) return;
            touchTaps?.Reset();
            e.Handled = true;
            await vm.ActivateProfileFromUserAsync(selected);
        };
        list.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || list.SelectedItem is not ServerItemViewModel selected) return;
            e.Handled = true;
            await vm.ActivateProfileFromUserAsync(selected);
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetIsScrollChainingEnabled(list, true);
        panel.Children.Add(list);
        var emptySubscription = Label("Обновите подписку для получения профилей");
        emptySubscription.HorizontalAlignment = HorizontalAlignment.Center;
        emptySubscription.Bind(Visual.IsVisibleProperty, new Binding(nameof(MainViewModel.IsSelectedSubscriptionEmpty)));
        panel.Children.Add(emptySubscription);
        const double buttonRowSpacing = 6;
        var testActions = new WrapPanel { ItemSpacing = 8, LineSpacing = buttonRowSpacing, VerticalAlignment = VerticalAlignment.Bottom };
        var testOne = Button("Проверить сервер", () => vm.ProbeSelectedCommand.Execute(null));
        testOne.Bind(Control.IsEnabledProperty, new Binding("CanProbe")); testActions.Children.Add(testOne);
        var testAll = Button("Проверить все", () => vm.ProbeAllCommand.Execute(null));
        testAll.Bind(Control.IsEnabledProperty, new Binding("CanProbeAll")); testActions.Children.Add(testAll);
        testActions.Bind(Visual.IsVisibleProperty, new Binding("!HasPendingOperation"));

        var cancelTests = Button("Отменить проверку", () => vm.CancelProbesCommand.Execute(null));
        cancelTests.Classes.Add("danger");
        cancelTests.HorizontalAlignment = HorizontalAlignment.Left;
        cancelTests.VerticalAlignment = VerticalAlignment.Bottom;
        cancelTests.Bind(Visual.IsVisibleProperty, new MultiBinding
        {
            Converter = BoolConverters.And,
            Bindings = { new Binding("IsProbing"), new Binding("!HasPendingConnection") }
        });
        var cancelConnection = Button("Отменить подключение", () => vm.CancelConnectionCommand.Execute(null));
        cancelConnection.Bind(ContentControl.ContentProperty, new Binding("CancelConnectionText") { Converter = new TranslationConverter() });
        cancelConnection.Bind(Control.IsEnabledProperty, new Binding("CanCancelConnection"));
        cancelConnection.Bind(Visual.IsVisibleProperty, new Binding("HasPendingConnection"));
        cancelConnection.Classes.Add("danger");
        cancelConnection.HorizontalAlignment = HorizontalAlignment.Left;
        cancelConnection.VerticalAlignment = VerticalAlignment.Bottom;

        var testSlot = new SubscriptionActionSlot(testActions) { Children = { testActions, cancelTests, cancelConnection } };
        var probeMode = Button("", () => { });
        void UpdateProbeMode() => probeMode.Content = L.T(UserSettings.Current.ServerProbeMode == ServerProbeMode.Fast
            ? "Проверка: Быстрая" : "Проверка: Точная (HTTPS)");
        probeMode.Click += (_, _) => { vm.ToggleProbeMode(); UpdateProbeMode(); };
        UpdateProbeMode();
        ToolTip.SetTip(probeMode, L.T("Метод быстрой проверки зависит от платформы и протокола: TCP, HTTP или TLS. Точная проверка выполняет HTTPS-запрос через профиль. Метод и время показаны в результате."));
        probeMode.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(new StackPanel { Spacing = buttonRowSpacing, Children = { testSlot, probeMode } });
        var lowest = new CheckBox { Content = L.T("Lowest: выбирать сервер с минимальной задержкой"), IsChecked = vm.IsLowestMode };
        var lowestHint = Label("Проверки: через 5, затем 10, затем каждые 15 минут. При смене сервера VPN переподключится.");
        lowestHint.Bind(Visual.IsVisibleProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = lowest });
        lowest.IsCheckedChanged += (_, _) => vm.SetLowestMode(lowest.IsChecked == true);
        panel.Children.Add(lowest);
        panel.Children.Add(lowestHint);
        var confirmationText = Label("Удалить выбранную подписку и все её серверы?");
        Action confirmedAction = () => vm.RemoveGroupCommand.Execute(null);
        var confirmationActions = new WrapPanel();
        var confirmationCard = new Border
        {
            Width = 300, MaxWidth = 300, Padding = new Thickness(18), CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Spacing = 12, Children = { confirmationText, confirmationActions } }
        };
        confirmationCard.Bind(Border.BackgroundProperty, confirmationCard.GetResourceObservable("CardBrush"));
        var confirmation = new Border { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), Child = confirmationCard, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        void CloseConfirmation() { confirmation.IsVisible = false; AppBackNavigation.Set(GoBack); }
        void ShowConfirmation(string text, Action action)
        {
            confirmationText.Text = L.T(text);
            confirmedAction = action;
            confirmation.IsVisible = true;
            AppBackNavigation.Set(CloseConfirmation);
        }
        var confirmDelete = Button("Удалить", () => { CloseConfirmation(); confirmedAction(); });
        confirmDelete.Margin = new Thickness(0, 0, 8, 0); confirmationActions.Children.Add(confirmDelete);
        confirmationActions.Children.Add(Button("Отмена", CloseConfirmation));

        string shareValue = "";
        Avalonia.Media.Imaging.Bitmap? shareBitmap = null;
        var shareTitle = Label("Поделиться");
        var shareQr = new Image { MaxWidth = 300, MaxHeight = 300, Stretch = Stretch.Uniform, IsVisible = false };
        var shareOptions = new Grid { ColumnDefinitions = new ColumnDefinitions("1.7*,8,*,8,*") };
        var shareMessage = Label("");
        var shareCard = new Border
        {
            MaxWidth = 360, Margin = new Thickness(8), Padding = new Thickness(20), CornerRadius = new CornerRadius(18),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Spacing = 14, Children = { shareTitle, shareQr, shareOptions, shareMessage } }
        };
        shareCard.Bind(Border.BackgroundProperty, shareCard.GetResourceObservable("CardBrush"));
        var shareOverlay = new Border { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), Child = shareCard };
        void CloseShare()
        {
            shareOverlay.IsVisible = false;
            shareQr.IsVisible = false;
            shareOptions.IsVisible = true;
            shareQr.Source = null;
            shareBitmap?.Dispose(); shareBitmap = null;
            AppBackNavigation.Set(GoBack);
        }
        void ShareError(string message)
        {
            shareMessage.Text = L.T(message);
            vm.Notice = message;
        }
        void AddShareOption(Button button, int column)
        {
            button.Margin = default;
            button.Padding = new Thickness(4, 10);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Content = new TextBlock { Text = button.Content?.ToString(), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            Grid.SetColumn(button, column);
            shareOptions.Children.Add(button);
        }
        async Task CopyShareAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
                if (clipboard is null) { ShareError("Буфер обмена недоступен. Попробуйте QR-код."); return; }
                await clipboard.SetTextAsync(shareValue);
                vm.Notice = "Ссылка скопирована в буфер обмена.";
                CloseShare();
            }
            catch { ShareError("Не удалось скопировать ссылку. Попробуйте QR-код."); }
        }
        void OpenShare(string title, string value)
        {
            shareTitle.Text = L.T(title);
            shareValue = value;
            shareMessage.Text = "";
            vm.Notice = "";
            shareOverlay.IsVisible = true;
            AppBackNavigation.Set(CloseShare);
        }
        var copyShare = AsyncButton("Буфер обмена", CopyShareAsync);
        AddShareOption(copyShare, 0);
        var qrShare = Button("QR-код", () =>
        {
            try
            {
                var bitmap = QrCodeImage.Create(shareValue);
                shareQr.Source = null;
                shareBitmap?.Dispose();
                shareBitmap = bitmap;
                shareQr.Source = bitmap;
                shareQr.IsVisible = true;
            }
            catch (QRCoder.Exceptions.DataTooLongException) { ShareError("Профиль слишком большой для QR-кода. Используйте буфер обмена."); }
            catch { ShareError("Не удалось создать QR-код."); }
        });
        AddShareOption(qrShare, 2);
        AddShareOption(Button("Отмена", CloseShare), 4);
        const string refreshIcon = "M19,9 A7,7 0 1 0 18,16 M19,4 L19,9 L14,9";
        const string shareIcon = "M8,12 L16,7 M8,12 L16,17 M8,12 A2,2 0 1 1 4,12 A2,2 0 1 1 8,12 M20,6 A2,2 0 1 1 16,6 A2,2 0 1 1 20,6 M20,18 A2,2 0 1 1 16,18 A2,2 0 1 1 20,18";
        const string deleteIcon = "M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,20 L17,20 L18,6 M10,10 L10,16 M14,10 L14,16";
        static Button ManagementButton(string label, string description, string geometry, Action action)
        {
            var button = Button(description, action);
            button.Margin = new Thickness(0, 0, 8, buttonRowSpacing);
            button.Padding = new Thickness(12, 10);
            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = 18, Height = 18, Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center, Data = Geometry.Parse(geometry),
                StrokeThickness = 1.7, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round
            };
            icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding(nameof(TemplatedControl.Foreground)) { Source = button });
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { icon, new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center } }
            };
            ToolTip.SetTip(button, L.T(description));
            AutomationProperties.SetName(button, L.T(description));
            return button;
        }
        var management = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refreshSelected = ManagementButton("Подписка", "Обновить выбранную подписку", refreshIcon, async () => await vm.RefreshSelectedSubscriptionAsync());
        refreshSelected.Bind(Control.IsEnabledProperty, new Binding(nameof(MainViewModel.CanRefreshSelectedSubscription)));
        management.Children.Add(refreshSelected);
        var refresh = ManagementButton("Все подписки", "Обновить все подписки", refreshIcon, async () => await vm.RefreshSubscriptionsAsync());
        refresh.Bind(Control.IsEnabledProperty, new Binding(nameof(MainViewModel.CanRefreshSubscriptions))); management.Children.Add(refresh);
        var shareSubscription = ManagementButton("Подписка", "Поделиться подпиской", shareIcon, () => { if (vm.SelectedSubscriptionUrl is { } value) OpenShare("Поделиться подпиской", value); });
        shareSubscription.Bind(Control.IsEnabledProperty, new Binding(nameof(MainViewModel.CanShareSubscription))); management.Children.Add(shareSubscription);
        var shareServer = ManagementButton("Сервер", "Поделиться сервером", shareIcon, () => { if (vm.SelectedServerUri is { } value) OpenShare("Поделиться сервером", value); });
        shareServer.Bind(Control.IsEnabledProperty, new Binding(nameof(MainViewModel.CanShareServer))); management.Children.Add(shareServer);
        var remove = ManagementButton("Подписка", "Удалить всю подписку", deleteIcon, () => ShowConfirmation("Удалить выбранную подписку и все её серверы?", () => vm.RemoveGroupCommand.Execute(null)));
        remove.Bind(Control.IsEnabledProperty, new MultiBinding
        {
            Converter = BoolConverters.And,
            Bindings = { new Binding(nameof(MainViewModel.CanManageProfiles)), new Binding(nameof(MainViewModel.SelectedGroup)) { Converter = ObjectConverters.IsNotNull } }
        });
        management.Children.Add(remove);
        var removeOne = ManagementButton("Сервер", "Удалить выбранный сервер", deleteIcon, () =>
        {
            if (vm.SelectedProfile is not { } selected) return;
            ShowConfirmation("Удалить выбранный сервер?", () =>
            {
                if (ReferenceEquals(vm.SelectedProfile, selected)) vm.RemoveProfileCommand.Execute(null);
            });
        });
        removeOne.Bind(Control.IsEnabledProperty, new MultiBinding
        {
            Converter = BoolConverters.And,
            Bindings = { new Binding(nameof(MainViewModel.CanManageProfiles)), new Binding(nameof(MainViewModel.SelectedProfile)) { Converter = ObjectConverters.IsNotNull } }
        });
        management.Children.Add(removeOne);
        var removeUnavailable = ManagementButton("Недоступные", "Удалить недоступные серверы", deleteIcon, () =>
        {
            if (!vm.CanRemoveUnavailable) return;
            if (vm.SelectedGroup is { SourceUrl: null } group && vm.Profiles.Count == 1)
            {
                var server = vm.Profiles[0];
                ShowConfirmation("Удалить выбранный сервер?", () =>
                {
                    if (vm.SelectedGroup == group && vm.Profiles.Count == 1 && ReferenceEquals(vm.Profiles[0], server))
                        vm.RemoveUnavailableCommand.Execute(null);
                });
            }
            else vm.RemoveUnavailableCommand.Execute(null);
        });
        removeUnavailable.Bind(Control.IsEnabledProperty, new Binding(nameof(MainViewModel.CanRemoveUnavailable)));
        management.Children.Add(removeUnavailable);
        panel.Children.Add(management);
        var status = Label(""); status.Bind(TextBlock.TextProperty, new Binding("SubscriptionStatus") { Converter = new TranslationConverter() }); panel.Children.Add(status);
        var operationNotice = Label(""); operationNotice.Bind(TextBlock.TextProperty, new Binding("DisplayNotice") { Converter = new TranslationConverter() }); panel.Children.Add(operationNotice);
        var page = Page("Подписки и профили", back, panel, mobileScroll: mobile);
        var root = new Grid { DataContext = vm };
        root.Children.Add(page);
        root.Children.Add(confirmation);
        root.Children.Add(shareOverlay);
        root.DetachedFromVisualTree += (_, _) =>
        {
            shareQr.Source = null;
            shareBitmap?.Dispose();
            shareBitmap = null;
        };
        owner.Content = root;
    }
}
