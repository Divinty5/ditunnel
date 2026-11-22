using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

public sealed partial class MapLightViewModel(double x, double y) : ObservableObject
{
    public double X { get; } = x;
    public double Y { get; } = y;
    [ObservableProperty] private double opacity;
    public double TargetOpacity { get; set; }
}

public sealed partial class MainViewModel : ViewModelBase
{
    public bool IsAndroid => OperatingSystem.IsAndroid();
    private readonly ProfileCatalog catalog;
    private readonly ProfileProbeHistory probeHistory;
    private readonly ProfileProbeCoordinator profileProbeCoordinator;
    private readonly ActiveTunnelMonitor tunnelMonitor;
    private readonly LowestProfileMonitor lowestMonitor = new();
    private readonly IProfileVpnEngine? engine;
    private readonly UserSettings selectionSettings;
    private readonly Action persistSelection;
    public AppPlatform Platform { get; }
    private bool storageAvailable = true;
    private readonly IServerCountryResolver? countryResolver;
    private readonly IInstalledApplicationProvider? installedApplicationProvider;
    private readonly Func<HttpClient>? directSubscriptionClient;
    private IEnumerable<ServerItemViewModel> highlightedProfiles = [];
    private ServerItemViewModel? activeProfile;
    private ServerItemViewModel? connectingProfile;
    private CancellationTokenSource? connectionCancellation;
    private CancellationTokenSource? probeCancellation;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly DispatcherTimer mapLightsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Random mapLightsRandom = new();
    private bool mapLightsFading;
    private int mapLightsFadeCursor;
    private int mapLightsChangeTick;
    private bool restoringProfileSelection;
    private bool profilesRemovedDuringProbe;
    private readonly DispatcherTimer subscriptionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool TunnelUnconfirmed => !tunnelMonitor.IsConfirmed &&
        (tunnelMonitor.MeasuresTunnel || Platform.ReleaseTarget is ReleaseTarget.LinuxX64 or ReleaseTarget.AndroidArm64);
    private VpnStatus? lastEngineStatus;
    private bool noticeDescribesOperation;
    private ProbeDiagnostics? noticeDiagnostics;
    public void RefreshLanguage()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Profiles) row.RefreshLanguage();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshSelectedSubscription))]
    private bool isProbing;
    public ObservableCollection<ServerItemViewModel> Profiles => catalog.VisibleProfiles;
    public ObservableCollection<ProfileGroup> Groups => catalog.Groups;
    [ObservableProperty] private ProfileGroup? selectedGroup;
    [ObservableProperty] private ServerItemViewModel? selectedProfile;
    [ObservableProperty] private bool isImportOpen;
    [ObservableProperty] private bool isManualImportOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshSelectedSubscription))]
    private bool isBusy;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private bool isCancellingConnection;
    public bool HasPendingConnection => IsConnecting || IsCancellingConnection || ConnectionState is VpnConnectionState.Connecting or VpnConnectionState.Reconnecting;
    public bool CanCancelConnection => HasPendingConnection && !IsCancellingConnection && ConnectionState != VpnConnectionState.Disconnecting;
    public string CancelConnectionText => IsCancellingConnection ? "Отменяем подключение…" : "Отменить подключение";
    public bool HasPendingOperation => HasPendingConnection || IsProbing;
    [ObservableProperty] private VpnConnectionState connectionState;
    [ObservableProperty] private string importText = "";
    [ObservableProperty] private string importName = "";
    [ObservableProperty] private string importMessage = "";
    [ObservableProperty] private string notice = "Добавьте подписку или конфигурацию, чтобы выбрать сервер.";
    public string StatusText => IsSubscriptionExhausted ? "Подписка исчерпана" : IsConnecting && ConnectionState == VpnConnectionState.Reconnecting
        ? "Подключаем VPN…"
        : ConnectionState switch
    {
        VpnConnectionState.Connecting => "Подключаем VPN…",
        VpnConnectionState.Reconnecting => "Восстанавливаем VPN…",
        VpnConnectionState.Connected when tunnelMonitor.Failures > 0 || TunnelUnconfirmed && tunnelMonitor.IsChecking => "Идёт проверка…",
        VpnConnectionState.Connected when TunnelUnconfirmed => "Идёт подключение…",
        VpnConnectionState.Connected => "VPN подключён",
        VpnConnectionState.Disconnecting => "Отключаем VPN…",
        VpnConnectionState.Error => "Не удалось подключиться",
        _ => IsConnecting ? "Подключаем VPN…" : "VPN отключён"
    };
    public string PowerText => IsCancellingConnection ? "Отменяем" : CanCancelConnection ? "Отменить" : ConnectionState == VpnConnectionState.Connected ? "Отключить" : "Подключить";
    public string CopyErrorText => "⧉";
    public bool HasConnectionError => ConnectionState == VpnConnectionState.Error && !string.IsNullOrWhiteSpace(Notice)
        || IsSubscriptionExhausted || noticeDiagnostics is not null || SelectedProbeDiagnostics is not null || Notice.Contains("Метод:", StringComparison.Ordinal);
    private ProbeDiagnostics? SelectedProbeDiagnostics =>
        SelectedProfile is { } server && probeHistory.Find(server.Profile) is { } snapshot
            ? snapshot.ConnectionResult?.Diagnostics ?? snapshot.Diagnostics : null;
    private string DiagnosticNotice => IsSubscriptionExhausted ? SubscriptionAccess.ExhaustedMessage + "\n" + Notice : Notice;
    public string DiagnosticText => noticeDiagnostics is { } current
        ? $"{DiagnosticNotice}\n\n{current.ToDisplayString()}"
        : !noticeDescribesOperation && ConnectionState != VpnConnectionState.Error &&
            !Notice.Contains("Метод:", StringComparison.Ordinal) && SelectedProbeDiagnostics is { } previous
                ? $"{DiagnosticNotice}\n\nПоследняя проверка выбранного профиля:\n{previous.ToDisplayString()}" : DiagnosticNotice;
    public string DisplayNotice => IsSubscriptionExhausted ? SubscriptionAccess.ExhaustedMessage : Notice.Split('\n', 2)[0];
    partial void OnNoticeChanged(string value)
    {
        noticeDescribesOperation = false;
        noticeDiagnostics = null;
        OnPropertyChanged(nameof(HasConnectionError));
        OnPropertyChanged(nameof(DisplayNotice));
    }

    private void SetOperationNotice(string message, ProbeDiagnostics? diagnostics = null)
    {
        Notice = message;
        noticeDescribesOperation = true;
        noticeDiagnostics = diagnostics;
        OnPropertyChanged(nameof(HasConnectionError));
    }
    internal double? DisplayDelay => ConnectionState == VpnConnectionState.Connected && tunnelMonitor.Failures == 0
        ? tunnelMonitor.MeasuresTunnel ? tunnelMonitor.Delay : tunnelMonitor.Delay ?? engine?.Status.DelayMilliseconds
        : null;
    public string ConnectionHint => DisplayDelay is { } delay
        ? $"≈  {delay:0} мс"
        : engine?.RequiresAdministrator == true
            ? "Для TUN запустите приложение от администратора"
            : Platform.ReleaseTarget == ReleaseTarget.AndroidArm64
                ? DisplayProfile?.Profile.Kind.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase) == true ? "Android VPN · AmneziaWG" : "Android VPN · Xray-core"
                : Platform.RuntimeDescription;
    public bool IsKillSwitchEnabled => Platform.SupportsKillSwitch && UserSettings.Current.KillSwitchEnabled;
    public bool ShowKillSwitchStatus => Platform.SupportsKillSwitch && (IsKillSwitchEnabled || engine?.IsNetworkProtectionActive == true);
    public string KillSwitchText => !Platform.SupportsKillSwitch ? ""
        : engine?.IsNetworkProtectionActive == true && ConnectionState != VpnConnectionState.Connected
        ? "Kill switch блокирует интернет. Подключите VPN или восстановите доступ к сети в настройках."
        : ConnectionState == VpnConnectionState.Connected && IsKillSwitchEnabled && engine?.IsNetworkProtectionActive == true
        ? "Kill switch активен"
        : ConnectionState == VpnConnectionState.Connected && IsKillSwitchEnabled
            ? "Kill switch не активен"
        : IsKillSwitchEnabled ? "Kill switch включён · активируется при подключении" : "";
    private ServerItemViewModel? DisplayProfile => connectingProfile ?? (ConnectionState is VpnConnectionState.Connected or
        VpnConnectionState.Reconnecting or VpnConnectionState.Disconnecting or VpnConnectionState.Error ? activeProfile : null) ?? SelectedProfile;
    internal ImportedProfile? RunningProfile => activeProfile?.Profile;
    internal DateTimeOffset? TunnelConnectedAt => lastEngineStatus?.ConnectedAt;
    public string SelectedName => DisplayProfile?.Name ?? L.T("Сервер не выбран");
    internal string DisplayProfileProbeText => DisplayProfile is { } server
        ? probeHistory.Get(server.Profile).ConnectionResult?.Message ?? server.ProbeStatusText : "Не проверен";
    public string SelectedSummary => DisplayProfile?.Summary.Replace(" · конфигурация сервера", "", StringComparison.OrdinalIgnoreCase) ?? "Импортируйте свою первую подписку";
    public string Tagline => "Твой VPN. Твой выбор.";
    public string ImportStorageDescription => Platform.ProfileStorageDescription;
    public string SubscriptionLimits
    {
        get
        {
            var usage = DisplayProfile?.Profile.Usage;
            if (usage is null) return "";
            var parts = new List<string>();
            if (usage.RemainingBytes is { } remaining) parts.Add($"{remaining / 1073741824m:0.##} ГБ осталось");
            if (usage.RemainingDays(DateTimeOffset.Now) is { } days) parts.Add($"{days} дн. осталось");
            return string.Join(" · ", parts);
        }
    }
    public bool IsSubscriptionExhausted => DisplayProfile?.Profile.Usage?.IsExhausted(DateTimeOffset.UtcNow) == true;
    public string SubscriptionTraffic => DisplayProfile?.Profile.Usage?.RemainingBytes is { } bytes
        ? bytes is > 0 and < 5368709.12m ? "<0,01 ГБ осталось" : $"{bytes / 1073741824m:0.##} ГБ осталось" : "";
    public string SubscriptionDays => DisplayProfile?.Profile.Usage?.RemainingDays(DateTimeOffset.UtcNow) is { } days ? $"{days} дн. осталось" : "";
    public bool HasBothSubscriptionLimits => SubscriptionTraffic.Length > 0 && SubscriptionDays.Length > 0;
    private static IBrush LimitBrush(bool exhausted, bool warning) => exhausted ? LatencyPalette.ForTheme(LatencyPalette.For(null, true), IsLightTheme)
        : warning ? LatencyPalette.ForTheme(LatencyPalette.For(2500), IsLightTheme)
        : new SolidColorBrush(Color.Parse(IsLightTheme ? "#241B35" : "#EEEAF7"));
    public IBrush SubscriptionTrafficBrush => LimitBrush(DisplayProfile?.Profile.Usage?.RemainingBytes == 0,
        DisplayProfile?.Profile.Usage?.RemainingBytes is > 0 and < 10737418240m);
    public IBrush SubscriptionDaysBrush => LimitBrush(DisplayProfile?.Profile.Usage?.RemainingDays(DateTimeOffset.UtcNow) == 0,
        DisplayProfile?.Profile.Usage?.RemainingDays(DateTimeOffset.UtcNow) is > 0 and < 7);
    public void RefreshSubscriptionPresentation()
    {
        foreach (var name in new[] { nameof(SubscriptionLimits), nameof(SubscriptionTraffic), nameof(SubscriptionDays), nameof(HasBothSubscriptionLimits),
            nameof(SubscriptionTrafficBrush), nameof(SubscriptionDaysBrush), nameof(IsSubscriptionExhausted), nameof(DisplayNotice),
            nameof(HasConnectionError) }) OnPropertyChanged(name);
        NotifyConnectionPresentation();
    }
    public string ProfileCount => $"Серверов: {Profiles.Count}";
    public string AppVersion => UserSettings.Version;
    private static bool IsLightTheme => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
    public IBrush PowerBrush => new SolidColorBrush(Color.Parse(IsLightTheme ? "#E9DFFA" : "#292039"));
    internal VpnConnectionState DisplayConnectionState => ConnectionPalette.DisplayState(ConnectionState,
        IsSubscriptionExhausted, tunnelMonitor.Failures > 0 || TunnelUnconfirmed, IsConnecting);
    public IBrush PowerBorderBrush => new SolidColorBrush(ConnectionPalette.AccentFor(DisplayConnectionState));
    public IBrush PowerGlowBrush => DisplayConnectionState == VpnConnectionState.Disconnected
        ? new SolidColorBrush(Color.Parse("#18000000"))
        : new SolidColorBrush(ConnectionPalette.AccentFor(DisplayConnectionState), .25);
    public IBrush ConnectionStateBrush => new SolidColorBrush(ConnectionPalette.ForegroundFor(DisplayConnectionState, IsLightTheme));

    private void NotifyConnectionPresentation()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayConnectionState));
        OnPropertyChanged(nameof(PowerBorderBrush));
        OnPropertyChanged(nameof(PowerGlowBrush));
        OnPropertyChanged(nameof(ConnectionStateBrush));
        UpdateHighlightedProfiles();
    }

    private void UpdateHighlightedProfiles()
    {
        var state = DisplayConnectionState;
        var rows = catalog.FindVisibleRows(state == VpnConnectionState.Disconnected ? null : (connectingProfile ?? activeProfile)?.Profile);
        if (!ReferenceEquals(rows, highlightedProfiles))
        {
            foreach (var previous in highlightedProfiles)
                if (!rows.Contains(previous)) previous.ActiveConnectionBrush = null;
            highlightedProfiles = rows;
        }
        if (!rows.Any()) return;

        var accent = ConnectionPalette.AccentFor(state);
        var background = IsLightTheme ? 255 : 18;
        byte Blend(byte channel) => (byte)((channel + 2 * background) / 3);
        var color = Color.FromRgb(Blend(accent.R), Blend(accent.G), Blend(accent.B));
        IBrush? brush = null;
        foreach (var row in rows)
            if (row.ActiveConnectionBrush is not ImmutableSolidColorBrush current || current.Color != color)
                row.ActiveConnectionBrush = brush ??= new ImmutableSolidColorBrush(color);
    }
    public IBrush DelayBrush => LatencyPalette.For(DisplayDelay);
    public bool HasDisplayDelay => DisplayDelay is not null;
    public IBrush ConnectionHintBrush => ConnectionState == VpnConnectionState.Connected && DisplayDelay is not null
        ? DelayBrush
        : new SolidColorBrush(Color.Parse(IsLightTheme ? "#665477" : "#B7ABC8"));
    [ObservableProperty] private bool mapLightsVisible;
    public ObservableCollection<MapLightViewModel> MapLights { get; } = CreateMapLights();
    public Avalonia.Media.Imaging.Bitmap? SelectedFlag => DisplayProfile?.FlagImage;
    public bool HasSelectedFlag => SelectedFlag is not null;
    public bool CanImport => storageAvailable && !IsBusy && !IsProbing && ConnectionState != VpnConnectionState.Disconnecting;
    public bool CanRefreshSubscriptions => storageAvailable && !refreshingSubscriptions && !IsBusy && !IsProbing;
    public bool CanRefreshSelectedSubscription => CanRefreshSubscriptions && SelectedSubscriptionUrl is not null;
    public bool CanManageProfiles => storageAvailable && !IsBusy && !IsConnecting && !IsProbing;
    public bool CanSelectProfile => !IsBusy && !HasPendingConnection && !IsCancellingConnection && !IsProbing && ConnectionState != VpnConnectionState.Disconnecting;
    internal bool IsRestoringProfileSelection => restoringProfileSelection;
    internal event Action? ProfileOrderChanged;
    public bool CanConnect => CanCancelConnection || (engine is not null && !IsCancellingConnection && !IsBusy && !IsProbing && ConnectionState != VpnConnectionState.Disconnecting);
    // A latency test never changes the selected VPN connection, so it remains safe while TUN is active.
    public bool CanProbe => CanProbeAll && SelectedProfile is not null;
    public bool CanProbeAll => !IsBusy && !HasPendingOperation && ConnectionState != VpnConnectionState.Disconnecting
        && profileProbeCoordinator.IsAvailable && Profiles.Count != 0;
    public int UnavailableProfileCount => Profiles.Count(server => server.IsUnavailable);
    public bool CanRemoveUnavailable => !IsBusy && !IsConnecting && UnavailableProfileCount > 0;
    public string? SelectedSubscriptionUrl => SelectedGroup?.SourceUrl;
    public bool IsSelectedSubscriptionEmpty => SelectedSubscriptionUrl is not null && Profiles.Count == 0;
    public string? SelectedServerUri => ProfileShareFormatter.CreateLink(SelectedProfile?.Profile);
    public bool CanShareSubscription => SelectedSubscriptionUrl is not null;
    public bool CanShareServer => SelectedServerUri is not null;
    public bool IsLowestMode => selectionSettings.LowestMode;

    public MainViewModel() : this(null) { }
    public MainViewModel(IProfileVpnEngine? engine, IProfileStore? store = null, IServerProbe? probe = null, IServerCountryResolver? countryResolver = null,
        IInstalledApplicationProvider? installedApplicationProvider = null, AppPlatform? platform = null, UserSettings? selectionSettings = null,
        Action? persistSelection = null, Func<HttpClient>? directSubscriptionClient = null)
    {
        this.engine = engine;
        this.selectionSettings = selectionSettings ?? UserSettings.Current;
        this.persistSelection = persistSelection ?? (() => this.selectionSettings.Save());
        Platform = platform ?? App.Platform;
        this.countryResolver = countryResolver;
        this.installedApplicationProvider = installedApplicationProvider;
        this.directSubscriptionClient = directSubscriptionClient;
        catalog = new(store ?? new UnavailableProfileStore("Защищённое хранилище профилей не настроено."));
        probeHistory = new(catalog);
        profileProbeCoordinator = new(probe, Platform.ReleaseTarget);
        tunnelMonitor = new(probe);
        if (engine is not null) engine.StatusChanged += EngineStatusChanged;
        mapLightsTimer.Tick += (_, _) => AdvanceMapLights();
        subscriptionTimer.Tick += (_, _) => RefreshSubscriptionPresentation();
        subscriptionTimer.Start();
        try
        {
            catalog.Load();
            RebuildGroups();
            ConnectionState = engine?.Status.State ?? VpnConnectionState.Disconnected;
            if (ConnectionState == VpnConnectionState.Connected)
            {
                var running = engine?.ActiveProfile;
                if (running is not null)
                {
                    ApplyEngineStatus(engine!.Status, running);
                }
                else activeProfile = SelectedProfile;
                NotifyConnectionPresentation();
                Notice = engine?.Status.Message ?? "VPN подключён.";
            }
            else if (SelectedProfile is not null) Notice = "Выберите профиль и сервер для подключения.";
            if (engine?.Status is { State: VpnConnectionState.Error, Probe: not null } failed)
                ApplyEngineStatus(failed, engine.ActiveProfile);
        }
        catch (ProfileStoreUnavailableException error) { Notice = error.Message; storageAvailable = false; }
        catch { Notice = "Не удалось прочитать сохранённые профили. Исходный файл не изменён."; storageAvailable = false; }
        if (IsLowestMode) StartLowestMode(checkImmediately: false);
    }

    partial void OnConnectionStateChanged(VpnConnectionState value)
    {
        if (value != VpnConnectionState.Connected) tunnelMonitor.ResetFailures();
        NotifyConnectionCancellation();
        if (value == VpnConnectionState.Connected)
        {
            StartActiveLatencyChecks();
            mapLightsFading = false;
            MapLightsVisible = true;
            foreach (var light in MapLights) light.TargetOpacity = 0;
            foreach (var light in MapLights.OrderBy(_ => mapLightsRandom.Next()).Take(10)) light.TargetOpacity = .7 + mapLightsRandom.NextDouble() * .25;
            mapLightsChangeTick = 0;
            mapLightsTimer.Start();
        }
        else if (MapLightsVisible)
        {
            mapLightsFading = true;
            mapLightsFadeCursor = 0;
            mapLightsTimer.Start();
        }
        if (value != VpnConnectionState.Connected) tunnelMonitor.Stop();
        OnPropertyChanged(nameof(ShowKillSwitchStatus));
        NotifyConnectionPresentation();
        OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(CopyErrorText)); OnPropertyChanged(nameof(HasConnectionError)); OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay)); OnPropertyChanged(nameof(KillSwitchText)); OnPropertyChanged(nameof(DelayBrush)); OnPropertyChanged(nameof(ConnectionHintBrush)); OnPropertyChanged(nameof(PowerBrush)); OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag)); OnPropertyChanged(nameof(SubscriptionLimits)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
    }
    private void AdvanceMapLights()
    {
        if (mapLightsFading)
        {
            if (mapLightsFadeCursor >= MapLights.Count)
            {
                mapLightsTimer.Stop();
                MapLightsVisible = false;
                return;
            }

            MapLights[mapLightsFadeCursor].TargetOpacity = 0;
            AdvanceMapLightOpacities();
            if (MapLights[mapLightsFadeCursor].Opacity <= .01) mapLightsFadeCursor++;
            return;
        }

        if (++mapLightsChangeTick >= 40)
        {
            mapLightsChangeTick = 0;
            var light = MapLights[mapLightsRandom.Next(MapLights.Count)];
            light.TargetOpacity = light.TargetOpacity > .1 ? 0 : .7 + mapLightsRandom.NextDouble() * .25;
        }
        AdvanceMapLightOpacities();
    }

    private void AdvanceMapLightOpacities()
    {
        foreach (var light in MapLights)
        {
            var difference = light.TargetOpacity - light.Opacity;
            light.Opacity += Math.Clamp(difference, -.02, .02);
        }
    }

    private static ObservableCollection<MapLightViewModel> CreateMapLights() =>
    [
        // Coordinates match the twenty individual SVG layers in Assets.
        new(333.38, 212.21),
        new(368.27, 169.31),
        new(359.42, 297.96),
        new(470.76, 360.54),
        new(480.13, 437.08),
        new(587.2, 446.67),
        new(514.76, 512.66),
        new(791.56, 158.38),
        new(810.44, 173.7),
        new(783.56, 209.34),
        new(859.56, 158.25),
        new(893.38, 159.47),
        new(967.2, 144.61),
        new(938.84, 253.16),
        new(867.87, 398.24),
        new(924.67, 490.62),
        new(1143.16, 259.2),
        new(1317.33, 211.53),
        new(1168.62, 147.61),
        new(1395.02, 480.07)
    ];
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanRemoveUnavailable)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); OnPropertyChanged(nameof(CanConnect)); }
    partial void OnIsProbingChanged(bool value) { OnPropertyChanged(nameof(HasPendingOperation)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanRemoveUnavailable)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); }
    partial void OnIsConnectingChanged(bool value) { NotifyConnectionCancellation(); NotifyConnectionPresentation(); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanRemoveUnavailable)); }
    partial void OnIsCancellingConnectionChanged(bool value) => NotifyConnectionCancellation();
    public async Task PrepareToCloseAsync(TimeSpan timeout)
    {
        try { await ShutdownAsync().WaitAsync(timeout); }
        catch { Notice = "Окно закрывается; независимый сетевой модуль продолжит восстановление сети."; }
    }

    public async Task ShutdownAsync()
    {
        subscriptionTimer.Stop();
        lifetimeCancellation.Cancel();
        connectionCancellation?.Cancel();
        probeCancellation?.Cancel();
        lowestMonitor.Stop();
        tunnelMonitor.Stop();
        if (engine is null) return;
        await engine.DisconnectAsync();
        engine.StatusChanged -= EngineStatusChanged;
        await engine.DisposeAsync();
    }
}
