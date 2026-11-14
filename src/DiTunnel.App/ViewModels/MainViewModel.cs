using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

public sealed record ProfileGroup(string Id, string Name, string? SourceUrl = null)
{
    public override string ToString() => Name;
}

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
    private readonly List<ImportedProfile> allProfiles = [];
    private readonly List<EmptySubscription> emptySubscriptions = [];
    private readonly IProfileVpnEngine? engine;
    private readonly IProfileStore profileStore;
    private readonly UserSettings selectionSettings;
    private readonly Action persistSelection;
    public AppPlatform Platform { get; }
    private bool storageAvailable = true;
    private readonly IServerProbe? probe;
    private readonly IServerCountryResolver? countryResolver;
    private readonly IInstalledApplicationProvider? installedApplicationProvider;
    private readonly Dictionary<string, ProbeSnapshot> probeSnapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingProbes = new(StringComparer.Ordinal);
    private ServerItemViewModel? activeProfile;
    private CancellationTokenSource? connectionCancellation;
    private CancellationTokenSource? probeCancellation;
    private CancellationTokenSource? lowestCancellation;
    private CancellationTokenSource? activeLatencyCancellation;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly DispatcherTimer mapLightsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Random mapLightsRandom = new();
    private bool mapLightsFading;
    private int mapLightsFadeCursor;
    private int mapLightsChangeTick;
    private bool suppressProfileReconnect;
    private double? activeDelayMilliseconds;
    private readonly DispatcherTimer subscriptionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool activeTunnelConfirmed;
    private bool activeTunnelProbeRunning;
    private bool TunnelUnconfirmed => !activeTunnelConfirmed &&
        (probe is IActiveTunnelProbe || Platform.ReleaseTarget is ReleaseTarget.LinuxX64 or ReleaseTarget.AndroidArm64);
    private VpnStatus? lastEngineStatus;
    private bool noticeDescribesOperation;
    private ProbeDiagnostics? noticeDiagnostics;
    private int activeProbeFailures;
    private int activeTransportFailures;
    private bool healthRecoveryAttempted;
    private bool healthFailoverAttempted;
    private int refreshToastGeneration;
    private bool refreshingSubscriptions;
    [ObservableProperty] private string subscriptionStatus = "";
    [ObservableProperty] private string refreshToast = "";
    [ObservableProperty] private bool isRefreshToastVisible;
    [ObservableProperty] private IBrush refreshToastBackground = new SolidColorBrush(Color.Parse("#D92EBE39"));

    public Task RefreshSubscriptionsAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null, bool showToast = true)
        => RefreshSubscriptionsCoreAsync(sourceId: null, download, showToast, includeEmpty: true);

    internal Task RefreshSubscriptionsOnStartupAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null)
        => RefreshSubscriptionsCoreAsync(sourceId: null, download, showToast: false, includeEmpty: false);

    public Task RefreshSelectedSubscriptionAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null)
        => CanRefreshSelectedSubscription && SelectedGroup is { } group
            ? RefreshSubscriptionsCoreAsync(group.Id, download, showToast: true, includeEmpty: true) : Task.CompletedTask;

    private async Task RefreshSubscriptionsCoreAsync(string? sourceId, Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download,
        bool showToast, bool includeEmpty)
    {
        if (!CanRefreshSubscriptions || !storageAvailable) return;
        var sources = Groups.Where(g => g.SourceUrl is not null && (sourceId is null || g.Id == sourceId)).ToArray();
        if (!includeEmpty)
        {
            var populatedSources = allProfiles.Select(profile => profile.SourceId).ToHashSet(StringComparer.Ordinal);
            sources = sources.Where(group => populatedSources.Contains(group.Id)).ToArray();
        }
        if (sources.Length == 0) return;
        var selectedOnly = sourceId is not null;
        download ??= (url, token) => new ProfileImporter(timeout: TimeSpan.FromSeconds(10)).ImportAsync(url, token);
        refreshingSubscriptions = true;
        OnPropertyChanged(nameof(CanRefreshSubscriptions));
        OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        if (showToast) IsBusy = true;
        SubscriptionStatus = selectedOnly ? "Обновляем подписку…" : "Обновляем подписки…";
        var updated = 0; var failed = 0;
        var selectedContent = SelectedProfile?.Profile.Content;
        try
        {
            using var concurrency = new SemaphoreSlim(4);
            var downloads = await Task.WhenAll(sources.Select(async source =>
            {
                await concurrency.WaitAsync(lifetimeCancellation.Token);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var fresh = await download(source.SourceUrl!, deadline.Token).WaitAsync(deadline.Token);
                    return (Source: source, Fresh: fresh, Error: (Exception?)null);
                }
                catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { return (Source: source, Fresh: (IReadOnlyList<ImportedProfile>)Array.Empty<ImportedProfile>(), Error: error); }
                finally { concurrency.Release(); }
            }));
            // Background refresh must not replace the selected profile while a connection
            // or another profile operation is using it.
            if (!showToast && (IsBusy || HasPendingOperation)) return;
            if (!showToast) selectedContent = SelectedProfile?.Profile.Content;
            foreach (var item in downloads)
            {
                lifetimeCancellation.Token.ThrowIfCancellationRequested();
                if (item.Fresh.Count == 0) { failed++; continue; }
                try
                {
                    var replacement = item.Fresh.DistinctBy(p => p.Content).Select(p => p with
                        { SourceId = item.Source.Id, SourceName = item.Source.Name, SourceUrl = item.Source.SourceUrl }).ToArray();
                    var next = allProfiles.Where(p => p.SourceId != item.Source.Id).Concat(replacement).ToArray();
                    var retained = replacement.Select(ProfileKey).ToHashSet(StringComparer.Ordinal);
                    var remainingEmpty = emptySubscriptions.Where(g => g.SourceId != item.Source.Id).ToArray();
                    profileStore.SaveLibrary(new(next, remainingEmpty));
                    foreach (var previous in allProfiles.Where(p => p.SourceId == item.Source.Id))
                        if (!retained.Contains(ProfileKey(previous))) probeSnapshots.Remove(ProfileKey(previous));
                    allProfiles.Clear(); allProfiles.AddRange(next);
                    emptySubscriptions.Clear(); emptySubscriptions.AddRange(remainingEmpty);
                    updated++;
                }
                catch { failed++; }
            }
            RebuildGroups();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Content == selectedContent) ?? SelectedProfile;
            if (activeProfile is { } active)
            {
                var refreshed = allProfiles.FirstOrDefault(p => p.SourceId == active.Profile.SourceId && p.Content == active.Profile.Content);
                if (refreshed is null && allProfiles.FirstOrDefault(p => p.SourceId == active.Profile.SourceId) is { } sourcePeer)
                    refreshed = active.Profile with { Usage = sourcePeer.Usage };
                if (refreshed is not null) activeProfile = Profiles.FirstOrDefault(row => row.Profile == refreshed) ?? new ServerItemViewModel(refreshed);
            }
            RefreshSubscriptionPresentation();
            var summary = selectedOnly
                ? failed == 0 ? "Подписка успешно обновлена" : "Не удалось обновить подписку"
                : failed == 0 ? $"Подписки успешно обновлены: {updated}"
                : updated == 0 ? $"Не удалось обновить: {failed}" : $"Обновлено: {updated}. Не удалось обновить: {failed}";
            var status = failed == 0 ? $"{summary} · {DateTime.Now:HH:mm}" : $"{summary}. Сохранены прежние серверы.";
            var invalidFormats = downloads.Count(item => item.Error is FormatException);
            var downloadErrors = downloads.Count(item => item.Error is HttpRequestException or OperationCanceledException);
            if (invalidFormats > 0) status += $" Ошибки формата: {invalidFormats}.";
            if (downloadErrors > 0) status += $" Ошибки загрузки: {downloadErrors}.";
            SubscriptionStatus = status;
            if (showToast)
            {
                RefreshToast = summary;
                RefreshToastBackground = new SolidColorBrush(Color.Parse(failed == 0 ? "#D92EBE39" : "#D9C8324A"));
                IsRefreshToastVisible = true;
                _ = HideRefreshToastAsync(++refreshToastGeneration);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        finally
        {
            refreshingSubscriptions = false;
            if (showToast) IsBusy = false;
            OnPropertyChanged(nameof(CanRefreshSubscriptions));
            OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        }
    }

    private async Task HideRefreshToastAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), lifetimeCancellation.Token);
            if (generation == refreshToastGeneration) IsRefreshToastVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Profiles) row.RefreshLanguage();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshSelectedSubscription))]
    private bool isProbing;
    public ObservableCollection<ServerItemViewModel> Profiles { get; } = [];
    public ObservableCollection<ProfileGroup> Groups { get; } = [];
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
        VpnConnectionState.Connected when activeProbeFailures > 0 => "Туннель запущен · Проверяем связь",
        VpnConnectionState.Connected when TunnelUnconfirmed => "Туннель VPN запущен",
        VpnConnectionState.Connected => "VPN подключён",
        VpnConnectionState.Disconnecting => "Отключаем VPN…",
        VpnConnectionState.Error => "Не удалось подключиться",
        _ => "VPN отключён"
    };
    public string PowerText => IsCancellingConnection ? "Отменяем" : CanCancelConnection ? "Отменить" : ConnectionState == VpnConnectionState.Connected ? "Отключить" : "Подключить";
    public string CopyErrorText => "⧉";
    public bool HasConnectionError => ConnectionState == VpnConnectionState.Error && !string.IsNullOrWhiteSpace(Notice)
        || IsSubscriptionExhausted || noticeDiagnostics is not null || SelectedProbeDiagnostics is not null || Notice.Contains("Метод:", StringComparison.Ordinal);
    private ProbeDiagnostics? SelectedProbeDiagnostics => SelectedProfile is { } server
        && probeSnapshots.TryGetValue(ProfileKey(server.Profile), out var snapshot) ? snapshot.Diagnostics : null;
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
    private double? DisplayDelay => ConnectionState == VpnConnectionState.Connected && activeProbeFailures == 0
        ? probe is IActiveTunnelProbe ? activeDelayMilliseconds : activeDelayMilliseconds ?? engine?.Status.DelayMilliseconds
        : null;
    public string ConnectionHint => DisplayDelay is { } delay
        ? $"≈  {delay:0} мс"
        : engine?.RequiresAdministrator == true
            ? "Для TUN запустите приложение от администратора"
            : Platform.ReleaseTarget == ReleaseTarget.AndroidArm64
                ? SelectedProfile?.Profile.Kind.Equals("AmneziaWG", StringComparison.OrdinalIgnoreCase) == true ? "Android VPN · AmneziaWG" : "Android VPN · Xray-core"
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
    public void RefreshConnectionPolicy()
    {
        OnPropertyChanged(nameof(IsKillSwitchEnabled));
        OnPropertyChanged(nameof(KillSwitchText));
        OnPropertyChanged(nameof(ShowKillSwitchStatus));
    }
    public bool CanRestoreNetwork => engine is INetworkRecoveryEngine;
    public async Task RestoreNetworkAsync()
    {
        if (engine is not INetworkRecoveryEngine recovery) return;
        CancelProbes();
        await CancelConnectionAsync();
        await recovery.RestoreNetworkAsync(lifetimeCancellation.Token);
        UserSettings.Current.KillSwitchEnabled = false;
        UserSettings.Current.Save();
        RefreshConnectionPolicy();
    }

    public async Task ApplyNetworkSettingsAsync()
    {
        if (!UserSettings.Current.KillSwitchEnabled && engine?.IsNetworkProtectionActive == true
            && ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error
            && engine is INetworkRecoveryEngine recovery)
        {
            try { await recovery.RestoreNetworkAsync(lifetimeCancellation.Token); RefreshConnectionPolicy(); }
            catch { Notice = "Не удалось восстановить сеть. Повторите с правами администратора."; }
            return;
        }
        var target = activeProfile ?? SelectedProfile;
        if (engine is null || target is null || IsConnecting || ConnectionState != VpnConnectionState.Connected)
            return;

        IsConnecting = true;
        ConnectionState = VpnConnectionState.Reconnecting;
        activeDelayMilliseconds = null;
        var cancellation = connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        Notice = "Применяем сетевые настройки и безопасно переподключаем VPN…";
        try
        {
            await Task.Delay(150, cancellation.Token);
            SubscriptionAccess.EnsureAvailable(target.Profile);
            await engine.SwitchAsync(target.Profile, cancellation.Token);
            activeProfile = target;
        }
        catch (OperationCanceledException) { Notice = "Применение сетевых настроек отменено."; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException or TimeoutException) { Notice = e.Message; }
        catch { Notice = "Не удалось применить сетевые настройки. VPN отключён; подключитесь снова."; }
        finally
        {
            ConnectionState = engine.Status.State;
            IsConnecting = false;
            cancellation.Dispose();
            if (ReferenceEquals(connectionCancellation, cancellation)) connectionCancellation = null;
        }
    }
    private ServerItemViewModel? DisplayProfile => ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Reconnecting
        ? activeProfile ?? SelectedProfile
        : SelectedProfile;
    public string SelectedName => DisplayProfile?.Name ?? L.T("Сервер не выбран");
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
            nameof(SubscriptionTrafficBrush), nameof(SubscriptionDaysBrush), nameof(IsSubscriptionExhausted), nameof(StatusText), nameof(DisplayNotice),
            nameof(HasConnectionError), nameof(PowerBorderBrush), nameof(PowerGlowBrush), nameof(ConnectionStateBrush) }) OnPropertyChanged(name);
    }
    public string ProfileCount => $"Серверов: {Profiles.Count}";
    public string AppVersion => UserSettings.Version;
    private static bool IsLightTheme => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
    public IBrush PowerBrush => new SolidColorBrush(Color.Parse(IsLightTheme ? "#E9DFFA" : "#292039"));
    private VpnConnectionState DisplayConnectionState => IsSubscriptionExhausted ? VpnConnectionState.Error
        : ConnectionState == VpnConnectionState.Connected &&
            (activeProbeFailures > 0 || TunnelUnconfirmed)
            ? VpnConnectionState.Connecting : ConnectionState;
    public IBrush PowerBorderBrush => new SolidColorBrush(ConnectionPalette.AccentFor(DisplayConnectionState));
    public IBrush PowerGlowBrush => DisplayConnectionState switch { VpnConnectionState.Connected => new SolidColorBrush(Color.Parse("#3830C86D")), VpnConnectionState.Error => new SolidColorBrush(Color.Parse("#40D94A58")), _ => new SolidColorBrush(Color.Parse("#18000000")) };
    public IBrush ConnectionStateBrush => DisplayConnectionState switch { VpnConnectionState.Connected => new SolidColorBrush(Color.Parse(IsLightTheme ? "#16804A" : "#66E491")), VpnConnectionState.Error => new SolidColorBrush(Color.Parse(IsLightTheme ? "#C83243" : "#FF7783")), _ => new SolidColorBrush(Color.Parse(IsLightTheme ? "#684096" : "#C9B9FF")) };
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
    public bool CanConnect => CanCancelConnection || (engine is not null && !IsCancellingConnection && !IsBusy && !IsProbing && ConnectionState != VpnConnectionState.Disconnecting);
    // A latency test never changes the selected VPN connection, so it remains safe while TUN is active.
    public bool CanProbe => CanProbeAll && SelectedProfile is not null;
    public bool CanProbeAll => !IsBusy && !HasPendingOperation && ConnectionState != VpnConnectionState.Disconnecting
        && probe is not null && Profiles.Count != 0;
    public int UnavailableProfileCount => Profiles.Count(server => server.ProbeTimedOut && !server.IsProbePending);
    public bool CanRemoveUnavailable => !IsBusy && !IsConnecting && UnavailableProfileCount > 0;
    public string? SelectedSubscriptionUrl => SelectedGroup?.SourceUrl;
    public bool IsSelectedSubscriptionEmpty => SelectedSubscriptionUrl is not null && Profiles.Count == 0;
    public string? SelectedServerUri => ProfileShareFormatter.CreateLink(SelectedProfile?.Profile);
    public bool CanShareSubscription => SelectedSubscriptionUrl is not null;
    public bool CanShareServer => SelectedServerUri is not null;
    public bool IsLowestMode => selectionSettings.LowestMode;

    public MainViewModel() : this(null) { }
    public MainViewModel(IProfileVpnEngine? engine, IProfileStore? store = null, IServerProbe? probe = null, IServerCountryResolver? countryResolver = null,
        IInstalledApplicationProvider? installedApplicationProvider = null, AppPlatform? platform = null, UserSettings? selectionSettings = null, Action? persistSelection = null)
    {
        this.engine = engine;
        this.selectionSettings = selectionSettings ?? UserSettings.Current;
        this.persistSelection = persistSelection ?? (() => this.selectionSettings.Save());
        Platform = platform ?? App.Platform;
        this.probe = probe;
        this.countryResolver = countryResolver;
        this.installedApplicationProvider = installedApplicationProvider;
        profileStore = store ?? new UnavailableProfileStore("Защищённое хранилище профилей не настроено.");
        if (engine is not null) engine.StatusChanged += EngineStatusChanged;
        mapLightsTimer.Tick += (_, _) => AdvanceMapLights();
        subscriptionTimer.Tick += (_, _) => RefreshSubscriptionPresentation();
        subscriptionTimer.Start();
        try
        {
            var library = profileStore.LoadLibrary();
            allProfiles.AddRange(library.Profiles);
            emptySubscriptions.AddRange(library.EmptySubscriptions);
            RebuildGroups();
            ConnectionState = engine?.Status.State ?? VpnConnectionState.Disconnected;
            if (ConnectionState == VpnConnectionState.Connected)
            {
                var running = engine?.ActiveProfile;
                if (running is not null)
                {
                    SelectedGroup = Groups.FirstOrDefault(group => group.Id == running.SourceId) ?? SelectedGroup;
                    SelectedProfile = Profiles.FirstOrDefault(row => row.Profile.Content == running.Content) ?? SelectedProfile;
                }
                activeProfile = SelectedProfile;
                Notice = engine?.Status.Message ?? "VPN подключён.";
            }
            else if (SelectedProfile is not null) Notice = "Выберите профиль и сервер для подключения.";
        }
        catch (ProfileStoreUnavailableException error) { Notice = error.Message; storageAvailable = false; }
        catch { Notice = "Не удалось прочитать сохранённые профили. Исходный файл не изменён."; storageAvailable = false; }
        if (IsLowestMode) StartLowestMode(checkImmediately: false);
    }

    private void EngineStatusChanged(object? sender, VpnStatus status)
    {
        // Capture the profile with its status, before a later switch changes the engine.
        var running = engine?.ActiveProfile;
        Dispatcher.UIThread.Post(() => ApplyEngineStatus(status, running));
    }

    internal void ApplyEngineStatus(VpnStatus status, ImportedProfile? running)
    {
        var profileChanged = running is not null && activeProfile?.Profile.Content != running.Content;
        var previousStatus = lastEngineStatus;
        var statusChanged = previousStatus != status || profileChanged;
        var newSession = status.State == VpnConnectionState.Connected &&
            (previousStatus?.State != status.State || previousStatus.ConnectedAt != status.ConnectedAt || profileChanged);
        var androidVerificationChanged = Platform.ReleaseTarget == ReleaseTarget.AndroidArm64 &&
            previousStatus?.DelayMilliseconds != status.DelayMilliseconds;
        lastEngineStatus = status;
        if (status.State != VpnConnectionState.Connected || newSession)
        {
            activeTunnelConfirmed = false;
            activeDelayMilliseconds = null;
            activeProbeFailures = 0;
            activeTransportFailures = 0;
        }
        if (status.State == VpnConnectionState.Connected)
        {
            var server = running is null || activeProfile?.Profile.Content == running.Content
                ? activeProfile
                : Profiles.FirstOrDefault(row => row.Profile.Content == running.Content);
            if (server is null && running is not null)
            {
                var stored = allProfiles.FirstOrDefault(profile => profile.Content == running.Content) ?? running;
                server = new ServerItemViewModel(stored);
                if (probeSnapshots.TryGetValue(ProfileKey(stored), out var snapshot))
                {
                    server.ProbeText = snapshot.Text;
                    server.ProbeMilliseconds = snapshot.Milliseconds;
                    server.ProbeTimedOut = snapshot.TimedOut;
                    server.IsProbeUnconfirmed = snapshot.Diagnostics is { Method: "TCP", ProfileConfirmed: false };
                    server.CountryCode = snapshot.CountryCode;
                }
            }
            if (server is not null) activeProfile = server;
            if (androidVerificationChanged && status.DelayMilliseconds is null)
            {
                activeTunnelConfirmed = false;
                activeDelayMilliseconds = null;
            }
            if (status.DelayMilliseconds is { } delay && (probe is not IActiveTunnelProbe ||
                androidVerificationChanged || newSession && Platform.ReleaseTarget == ReleaseTarget.AndroidArm64))
            {
                activeTunnelConfirmed = Platform.ReleaseTarget == ReleaseTarget.AndroidArm64;
                if (server is not null)
                {
                    server.ProbeText = $"HTTPS · {delay:0} мс";
                    server.ProbeMilliseconds = delay;
                    server.ProbeTimedOut = false;
                    server.IsProbeUnconfirmed = false;
                }
                activeDelayMilliseconds = delay;
            }
        }
        else if (status.State == VpnConnectionState.Disconnected) activeProfile = null;
        if (newSession && ConnectionState == status.State) StartActiveLatencyChecks();
        ConnectionState = status.State;
        // Connected -> Connected and external widget switches also need to invalidate
        // the header. Setting SelectedProfile alone still displays the old active server.
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(SelectedFlag));
        OnPropertyChanged(nameof(HasSelectedFlag));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SubscriptionLimits));
        NotifyConnectionDelay();
        if (statusChanged && status.Message is not null) SetOperationNotice(status.Message);
        RefreshSubscriptionPresentation();
    }
    partial void OnConnectionStateChanged(VpnConnectionState value)
    {
        if (value != VpnConnectionState.Connected)
        {
            activeProbeFailures = 0;
            activeTransportFailures = 0;
        }
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
        if (value != VpnConnectionState.Connected) StopActiveLatencyChecks();
        OnPropertyChanged(nameof(ShowKillSwitchStatus));
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(CopyErrorText)); OnPropertyChanged(nameof(HasConnectionError)); OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay)); OnPropertyChanged(nameof(KillSwitchText)); OnPropertyChanged(nameof(DelayBrush)); OnPropertyChanged(nameof(ConnectionHintBrush)); OnPropertyChanged(nameof(PowerBrush)); OnPropertyChanged(nameof(PowerBorderBrush)); OnPropertyChanged(nameof(PowerGlowBrush)); OnPropertyChanged(nameof(ConnectionStateBrush)); OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag)); OnPropertyChanged(nameof(SubscriptionLimits)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
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
    partial void OnIsConnectingChanged(bool value) { NotifyConnectionCancellation(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanRefreshSubscriptions)); OnPropertyChanged(nameof(CanManageProfiles)); OnPropertyChanged(nameof(CanRemoveUnavailable)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); OnPropertyChanged(nameof(CanConnect)); }
    partial void OnIsCancellingConnectionChanged(bool value) => NotifyConnectionCancellation();
    private void NotifyConnectionCancellation()
    {
        OnPropertyChanged(nameof(HasPendingConnection));
        OnPropertyChanged(nameof(HasPendingOperation));
        OnPropertyChanged(nameof(CanCancelConnection));
        OnPropertyChanged(nameof(CancelConnectionText));
        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanProbeAll));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanSelectProfile));
        OnPropertyChanged(nameof(PowerText));
    }
    [RelayCommand] private async Task CancelConnectionAsync()
    {
        if (!CanCancelConnection || engine is null) return;
        IsCancellingConnection = true;
        connectionCancellation?.Cancel();
        Notice = "Отменяем подключение и восстанавливаем сеть…";
        try
        {
            await engine.DisconnectAsync();
            Notice = "Подключение отменено.";
        }
        catch { Notice = "Не удалось отключить VPN; повторите отключение."; }
        finally { ConnectionState = engine.Status.State; IsCancellingConnection = false; }
    }
    partial void OnSelectedGroupChanged(ProfileGroup? value)
    {
        if (value is not null && selectionSettings.SelectedSourceId != value.Id)
        {
            selectionSettings.SelectedSourceId = value.Id;
            try { persistSelection(); } catch { }
        }
        var wasSuppressingReconnect = suppressProfileReconnect;
        suppressProfileReconnect = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in allProfiles.Where(p => p.SourceId == value?.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var key = ProfileKey(profile);
                var row = new ServerItemViewModel(profile) { IsProbePending = pendingProbes.Contains(key) };
                if (probeSnapshots.TryGetValue(key, out var snapshot))
                {
                    row.ProbeText = snapshot.Text;
                    row.ProbeMilliseconds = snapshot.Milliseconds;
                    row.ProbeTimedOut = snapshot.TimedOut;
                    row.IsProbeUnconfirmed = snapshot.Diagnostics is { Method: "TCP", ProfileConfirmed: false };
                    row.CountryCode = snapshot.CountryCode;
                }
                Profiles.Add(row);
            }
            selectionSettings.SelectedServers.TryGetValue(value?.Id ?? "", out var saved);
            SelectedProfile = Profiles.FirstOrDefault(row => activeProfile is not null && row.Profile.Content == activeProfile.Profile.Content)
                ?? Profiles.FirstOrDefault(row => saved is not null && ServerSelection.From(row.Profile).ContentHash == saved.ContentHash)
                ?? Profiles.FirstOrDefault(row => saved is not null && ServerSelection.From(row.Profile).IdentityHash == saved.IdentityHash)
                ?? Profiles.FirstOrDefault();
        }
        finally { suppressProfileReconnect = wasSuppressingReconnect; }
        OnPropertyChanged(nameof(ProfileCount)); OnPropertyChanged(nameof(CanProbeAll)); OnPropertyChanged(nameof(UnavailableProfileCount)); OnPropertyChanged(nameof(CanRemoveUnavailable));
        OnPropertyChanged(nameof(SelectedSubscriptionUrl)); OnPropertyChanged(nameof(CanShareSubscription));
        OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        OnPropertyChanged(nameof(IsSelectedSubscriptionEmpty));
        _ = ResolveCountriesAsync(Profiles.ToArray());
    }
    private async Task ResolveCountriesAsync(ServerItemViewModel[] rows)
    {
        if (countryResolver is null) return;
        foreach (var row in rows)
        {
            if (lifetimeCancellation.IsCancellationRequested) return;
            if (row.CountryCode is not null) continue;
            string? code;
            try { code = await countryResolver.ResolveAsync(row.Profile, lifetimeCancellation.Token); }
            catch { continue; }
            Dispatcher.UIThread.Post(() =>
            {
                var key = ProfileKey(row.Profile);
                var visible = Profiles.FirstOrDefault(candidate => ProfileKey(candidate.Profile) == key);
                if (visible is null || !ReferenceEquals(visible.Profile, row.Profile)) return;
                probeSnapshots[key] = GetSnapshot(visible.Profile) with { CountryCode = code };
                visible.CountryCode = code;
                if (SelectedProfile == visible) { OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag)); }
            });
        }
    }
    partial void OnSelectedProfileChanged(ServerItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasConnectionError));
        RefreshSubscriptionPresentation();
        if (value is not null && !suppressProfileReconnect)
        {
            var selected = ServerSelection.From(value.Profile);
            if (selectionSettings.SelectedServers.GetValueOrDefault(value.Profile.SourceId) != selected)
            {
                selectionSettings.SelectedServers[value.Profile.SourceId] = selected;
                try { persistSelection(); } catch { }
            }
        }
        OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag));
        OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(SubscriptionLimits)); OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay)); OnPropertyChanged(nameof(DelayBrush)); OnPropertyChanged(nameof(ConnectionHintBrush)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
        OnPropertyChanged(nameof(SelectedServerUri)); OnPropertyChanged(nameof(CanShareServer));
    }

    public void SelectProfileFromUser(ServerItemViewModel value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SelectedProfile = value;
        if (!suppressProfileReconnect && value != activeProfile &&
            ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Reconnecting or VpnConnectionState.Error)
        {
            healthRecoveryAttempted = false;
            healthFailoverAttempted = false;
            _ = ReconnectSelectedProfileSafelyAsync(value);
        }
    }

    private async Task ReconnectSelectedProfileSafelyAsync(ServerItemViewModel target)
    {
        try
        {
            await ReconnectSelectedProfileAsync(target);
        }
        catch
        {
            ConnectionState = engine?.Status.State ?? VpnConnectionState.Error;
            Notice = "Не удалось переключить сервер. VPN отключён; выберите профиль и подключитесь снова.";
        }
    }

    private async Task ReconnectSelectedProfileAsync(ServerItemViewModel target, bool restartCurrent = false)
    {
        if (engine is null || IsConnecting || ConnectionState is not (VpnConnectionState.Connected or VpnConnectionState.Reconnecting or VpnConnectionState.Error)
            || (!restartCurrent && target == activeProfile)) return;
        var wasConnected = ConnectionState == VpnConnectionState.Connected;
        var wasRecovering = ConnectionState == VpnConnectionState.Reconnecting;
        IsConnecting = true;
        ConnectionState = VpnConnectionState.Reconnecting;
        activeDelayMilliseconds = null;
        var cancellation = connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        Notice = restartCurrent ? $"Перезапускаем VPN через {target.Name}…" : $"Переключаемся на сервер {target.Name}…";
        try
        {
            if (wasConnected)
            {
                await Task.Delay(150, cancellation.Token);
                SubscriptionAccess.EnsureAvailable(target.Profile);
            await engine.SwitchAsync(target.Profile, cancellation.Token);
                activeProfile = target;
                return;
            }
            if (wasRecovering)
            {
                await engine.DisconnectAsync(cancellation.Token);
                if (SelectedProfile != target || cancellation.IsCancellationRequested) return;
            }
            if (SelectedProfile != target || cancellation.IsCancellationRequested)
            {
                return;
            }
            SubscriptionAccess.EnsureAvailable(target.Profile);
                    await engine.ConnectAsync(target.Profile, cancellation.Token);
            activeProfile = target;
        }
        catch (OperationCanceledException) { SetOperationNotice("Переключение сервера отменено."); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException or TimeoutException) { SetOperationNotice(e.Message); }
        catch { SetOperationNotice("Не удалось переключить сервер. Предыдущее подключение отключено; выберите сервер и подключитесь снова."); }
        finally
        {
            ConnectionState = engine.Status.State;
            IsConnecting = false;
            cancellation.Dispose();
            if (ReferenceEquals(connectionCancellation, cancellation)) connectionCancellation = null;
        }
    }
    private void RebuildGroups(string? preferred = null)
    {
        preferred ??= SelectedGroup?.Id ?? selectionSettings.SelectedSourceId;
        Groups.Clear();
        foreach (var group in allProfiles.GroupBy(p => p.SourceId))
        {
            var profile = group.First();
            Groups.Add(new(group.Key, profile.SourceName, profile.SourceUrl));
        }
        foreach (var subscription in emptySubscriptions)
            Groups.Add(new(subscription.SourceId, subscription.SourceName, subscription.SourceUrl));
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == preferred) ?? Groups.FirstOrDefault();
        // A record-equal selection does not raise a property change after importing into an existing group.
        OnSelectedGroupChanged(SelectedGroup);
    }
    [RelayCommand] private void OpenImport() { if (!CanImport) return; ImportMessage = ""; IsManualImportOpen = false; IsImportOpen = true; }
    [RelayCommand] private void OpenManualImport() { if (!CanImport) return; ImportMessage = ""; IsManualImportOpen = true; }
    [RelayCommand] private void CloseImport() { if (!IsBusy) { IsImportOpen = false; IsManualImportOpen = false; ImportText = ""; ImportName = ""; } }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task ConnectAsync()
    {
        if (CanCancelConnection) { await CancelConnectionAsync(); return; }
        if (!CanConnect) return;
        if (engine is null) { Notice = "Сетевой движок недоступен на этой платформе."; return; }
        IsConnecting = true;
        connectionCancellation = new CancellationTokenSource();
        try
        {
            await InitializeSplitDefaultsAsync().WaitAsync(connectionCancellation.Token);
            if (ConnectionState == VpnConnectionState.Connected)
            {
                await engine.DisconnectAsync();
                Notice = engine.Status.Message ?? "VPN отключён.";
            }
            else if (ConnectionState == VpnConnectionState.Reconnecting)
            {
                var target = activeProfile ?? SelectedProfile;
                if (target is null) Notice = "Сначала выберите сервер.";
                else
                {
                    Notice = $"Перезапускаем VPN через {target.Name}…";
                    await engine.DisconnectAsync(connectionCancellation.Token);
                    SubscriptionAccess.EnsureAvailable(target.Profile);
                    await engine.ConnectAsync(target.Profile, connectionCancellation.Token);
                    if (engine.Status.State == VpnConnectionState.Connected) activeProfile = target;
                }
            }
            else if (SelectedProfile is null) Notice = "Сначала выберите сервер.";
            else
            {
                healthRecoveryAttempted = false;
                healthFailoverAttempted = false;
                Notice = "Подключаем VPN…";
                SubscriptionAccess.EnsureAvailable(SelectedProfile.Profile);
                await engine.ConnectAsync(SelectedProfile.Profile, connectionCancellation.Token);
                if (engine.Status.State == VpnConnectionState.Connected)
                {
                    activeProfile = SelectedProfile;
                    Notice = engine.Status.Message ?? "VPN подключён";
                }
            }
        }
        catch (OperationCanceledException) { SetOperationNotice("Подключение отменено."); }
        catch (TimeoutException e) { SetOperationNotice(e.Message); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException) { SetOperationNotice(e.Message); }
        catch { SetOperationNotice("Не удалось изменить состояние VPN. Сетевые изменения отменены; проверьте права и доступность сервера."); }
        finally { ConnectionState = engine.Status.State; IsConnecting = false; connectionCancellation.Dispose(); connectionCancellation = null; }
    }
    [RelayCommand] private void RemoveProfile()
    {
        if (SelectedProfile is null || !CanManageProfiles) return;
        if (activeProfile?.Profile == SelectedProfile.Profile && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
        {
            _ = SwitchOrDisconnectAndRemoveAsync(p => p == SelectedProfile.Profile, "Сервер удалён.");
            return;
        }
        RemoveWhere(p => p == SelectedProfile.Profile, "Сервер удалён.");
    }

    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken cancellationToken = default) =>
        installedApplicationProvider?.GetInstalledApplicationsAsync(cancellationToken)
        ?? Task.FromResult<IReadOnlyList<InstalledApplication>>([]);

    public async Task InitializeSplitDefaultsAsync()
    {
        if (installedApplicationProvider is null) return;
        try
        {
            if (await UserSettings.Current.InitializeSplitTunnelDefaultsAsync(() => GetInstalledApplicationsAsync()))
                await ApplyNetworkSettingsAsync();
        }
        catch { Notice = "Не удалось применить начальные настройки раздельного туннелирования."; }
    }
    [RelayCommand] private void RemoveGroup()
    {
        if (SelectedGroup is not { } group || !CanManageProfiles) return;
        if (activeProfile?.Profile.SourceId == group.Id && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
        {
            _ = SwitchOrDisconnectAndRemoveAsync(p => p.SourceId == group.Id, "Профиль и все его серверы удалены.", group.Id);
            return;
        }
        RemoveWhere(p => p.SourceId == group.Id, "Профиль и все его серверы удалены.", deletedGroupId: group.Id);
    }
    [RelayCommand] private void RemoveUnavailable()
    {
        var sourceId = SelectedGroup?.Id;
        var unavailable = Profiles.Where(server => server.ProbeTimedOut && !server.IsProbePending)
            .Select(server => server.Profile.Content).ToHashSet(StringComparer.Ordinal);
        if (sourceId is null || unavailable.Count == 0 || !CanRemoveUnavailable) return;
        bool Predicate(ImportedProfile profile) => profile.SourceId == sourceId && unavailable.Contains(profile.Content);
        var message = $"Удалено недоступных серверов: {unavailable.Count}.";
        if (activeProfile is not null && Predicate(activeProfile.Profile) && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
            _ = SwitchOrDisconnectAndRemoveAsync(Predicate, message);
        else RemoveWhere(Predicate, message);
    }
    private async Task SwitchOrDisconnectAndRemoveAsync(Func<ImportedProfile, bool> predicate, string message, string? deletedGroupId = null)
    {
        if (engine is null) return;
        var selectedSourceId = SelectedGroup?.Id;
        IsConnecting = true;
        try
        {
            var remaining = allProfiles.Where(profile => !predicate(profile)).ToArray();
            var best = remaining
                .Select(profile => (Profile: profile, Delay: Profiles.FirstOrDefault(row => row.Profile.Content == profile.Content)?.ProbeMilliseconds))
                .OrderBy(candidate => candidate.Profile.SourceId == SelectedGroup?.Id ? 0 : 1)
                .ThenBy(candidate => candidate.Delay ?? double.MaxValue)
                .ThenBy(candidate => candidate.Profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Profile)
                .FirstOrDefault();
            if (best is null)
            {
                Notice = "Отключаем VPN перед удалением последнего профиля…";
                await engine.DisconnectAsync();
                activeProfile = null;
            }
            else
            {
                Notice = $"Переключаем VPN на сервер {best.Name} перед удалением…";
                await engine.SwitchAsync(best);
                activeProfile = Profiles.FirstOrDefault(row => row.Profile == best) ?? new ServerItemViewModel(best);
            }
            ConnectionState = engine.Status.State;
            RemoveWhere(predicate, message, deletedGroupId is null ? selectedSourceId : best?.SourceId, best?.Content, deletedGroupId);
        }
        catch (Exception error) { Notice = error is InvalidOperationException ? error.Message : "Не удалось отключить VPN; профиль не удалён."; }
        finally { IsConnecting = false; }
    }
    private void RemoveWhere(Func<ImportedProfile, bool> predicate, string message, string? preferredSourceId = null,
        string? preferredContent = null, string? deletedGroupId = null)
    {
        var remaining = allProfiles.Where(p => !predicate(p)).ToArray();
        try
        {
            if (!storageAvailable) throw new IOException();
            var remainingSources = remaining.Select(p => p.SourceId).ToHashSet(StringComparer.Ordinal);
            var nextEmpty = Groups.Where(group => group.Id != deletedGroupId && group.SourceUrl is not null && !remainingSources.Contains(group.Id))
                .Select(group => new EmptySubscription(group.Id, group.Name, group.SourceUrl!)).ToArray();
            profileStore.SaveLibrary(new(remaining, nextEmpty));
            preferredSourceId ??= SelectedGroup?.Id ?? activeProfile?.Profile.SourceId;
            preferredContent ??= activeProfile?.Profile.Content;
            foreach (var removed in allProfiles.Where(predicate)) probeSnapshots.Remove(ProfileKey(removed));
            allProfiles.Clear(); allProfiles.AddRange(remaining);
            emptySubscriptions.Clear(); emptySubscriptions.AddRange(nextEmpty);
            suppressProfileReconnect = true;
            try
            {
                RebuildGroups(preferredSourceId);
                if (preferredContent is not null)
                    SelectedProfile = Profiles.FirstOrDefault(row => row.Profile.Content == preferredContent) ?? SelectedProfile;
                if (ConnectionState == VpnConnectionState.Connected && activeProfile is { } running)
                {
                    var activeKey = ProfileKey(running.Profile);
                    activeProfile = Profiles.FirstOrDefault(row => ProfileKey(row.Profile) == activeKey) ?? running;
                }
            }
            finally { suppressProfileReconnect = false; }
            Notice = message;
        }
        catch { Notice = "Не удалось сохранить изменения. Профили не удалены."; }
    }
    [RelayCommand] private async Task ImportAsync()
    {
        if (!CanImport) return;
        IsBusy = true; ImportMessage = "Проверяем конфигурацию…";
        try
        {
            var input = ImportText.Trim();
            var importer = new ProfileImporter();
            var imported = await importer.ImportAsync(input);
            var isSubscription = Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https";
            var sourceUrl = isSubscription ? new UriBuilder(uri!) { Fragment = "" }.Uri.AbsoluteUri : null;
            var existing = sourceUrl is null ? null : Groups.FirstOrDefault(group => group.SourceUrl == sourceUrl);
            var sourceId = existing?.Id ?? Guid.NewGuid().ToString("N");
            var fragmentName = isSubscription && !string.IsNullOrWhiteSpace(uri!.Fragment)
                ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim()
                : null;
            var pathName = isSubscription
                ? Uri.UnescapeDataString(uri!.Segments.LastOrDefault()?.Trim('/') ?? "").Trim()
                : null;
            var sourceName = string.IsNullOrWhiteSpace(ImportName)
                ? existing?.Name ?? fragmentName ?? (string.IsNullOrWhiteSpace(pathName) ? null : pathName)
                    ?? (sourceUrl is null ? "Ручной профиль" : "Подписка") + $" {Groups.Count + 1}"
                : ImportName.Trim();
            var added = imported.Where(p => !allProfiles.Any(e => e.SourceId == sourceId && e.Content == p.Content))
                .Select(p => p with { SourceId = sourceId, SourceName = sourceName, SourceUrl = sourceUrl }).ToArray();
            if (!storageAvailable) { ImportMessage = "Хранилище недоступно. Сохранённый файл защищён от перезаписи."; return; }
            var remainingEmpty = emptySubscriptions.Where(g => g.SourceId != sourceId).ToArray();
            profileStore.SaveLibrary(new(allProfiles.Concat(added).ToArray(), remainingEmpty));
            allProfiles.AddRange(added);
            emptySubscriptions.Clear(); emptySubscriptions.AddRange(remainingEmpty);
            var keepActive = ConnectionState == VpnConnectionState.Connected && activeProfile is not null;
            suppressProfileReconnect = keepActive;
            try
            {
                RebuildGroups(keepActive ? activeProfile!.Profile.SourceId : sourceId);
                if (keepActive)
                    SelectedProfile = Profiles.FirstOrDefault(row => row.Profile.Content == activeProfile!.Profile.Content) ?? SelectedProfile;
            }
            finally { suppressProfileReconnect = false; }
            Notice = added.Length == 0 ? "Эти серверы уже есть в подписке."
                : importer.SkippedEntries > 0 ? $"Добавлено серверов: {added.Length}. Пропущено записей: {importer.SkippedEntries}."
                : $"Добавлено серверов: {added.Length}.";
            ImportText = ""; ImportName = ""; IsImportOpen = false;
        }
        catch (FormatException e) { ImportMessage = e.Message; }
        catch (HttpRequestException) { ImportMessage = "Не удалось импортировать. Проверьте адрес и доступность подписки."; }
        catch (OperationCanceledException) { ImportMessage = "Не удалось импортировать: сервер не ответил вовремя."; }
        catch { ImportMessage = "Не удалось импортировать или сохранить профили. Существующие профили сохранены."; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private Task ProbeSelectedAsync() => RunProbesAsync(false);
    [RelayCommand] private Task ProbeAllAsync() => RunProbesAsync(true);
    [RelayCommand] private void CancelProbes() => probeCancellation?.Cancel();

    public void SetLowestMode(bool enabled)
    {
        selectionSettings.LowestMode = enabled;
        try { selectionSettings.Save(); } catch { Notice = "Не удалось сохранить настройки режима lowest."; return; }
        OnPropertyChanged(nameof(IsLowestMode));
        lowestCancellation?.Cancel();
        if (enabled) StartLowestMode(checkImmediately: true);
        else Notice = "Режим lowest выключен.";
    }

    private void StartLowestMode(bool checkImmediately)
    {
        lowestCancellation?.Cancel();
        lowestCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        _ = RunLowestLoopAsync(lowestCancellation.Token, checkImmediately);
    }

    private async Task RunLowestLoopAsync(CancellationToken cancellationToken, bool checkImmediately)
    {
        var step = 0;
        try
        {
            if (checkImmediately)
            {
                var changed = await CheckLowestAsync(cancellationToken);
                step = changed ? 0 : 1;
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMinutes(step switch { 0 => 5, 1 => 10, _ => 15 });
                Notice = $"Lowest: следующая проверка через {(int)delay.TotalMinutes} мин.";
                await Task.Delay(delay, cancellationToken);
                var changed = await CheckLowestAsync(cancellationToken);
                step = changed ? 0 : Math.Min(step + 1, 2);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task<bool> CheckLowestAsync(CancellationToken cancellationToken)
    {
        if (!CanProbeAll || probe is null) return false;
        var servers = Profiles.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation = cancellation;
        IsProbing = true;
        try
        {
            var results = await ProbeServersAsync(servers, cancellation.Token);
            SortProfilesByProbe();
            return await SelectLowestProfileAsync();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Notice = "Проверка отменена."; return false; }
        catch (OperationCanceledException) { throw; }
        catch { Notice = "Lowest: не удалось проверить серверы."; return false; }
        finally
        {
            ClearPendingProbes();
            if (ReferenceEquals(probeCancellation, cancellation)) probeCancellation = null;
            IsProbing = false;
        }
    }

    private async Task RunProbesAsync(bool all)
    {
        if ((!all && !CanProbe) || (all && !CanProbeAll) || probe is null) return;
        var servers = all ? Profiles.ToArray() : [SelectedProfile!];
        IsProbing = true;
        probeCancellation = new CancellationTokenSource();
        try
        {
            SetOperationNotice(servers.Length == 1 ? $"Проверяем сервер {servers[0].Name}…" : $"Проверяем серверы: {servers.Length}…");
            var results = await ProbeServersAsync(servers, probeCancellation.Token);
            SortProfilesByProbe();
            var changed = all && IsLowestMode && await SelectLowestProfileAsync();
            if (!changed && all)
                Notice = $"Проверены: {servers.Length}. Ответили: {results.Count(result => result.Milliseconds is not null)}. Ошибки: {results.Count(result => !result.IsDeferred && result.Milliseconds is null)}.";
            else if (!changed) SetOperationNotice($"{servers[0].Name}: {results[0].Message}.", results[0].Diagnostics);
        }
        catch (OperationCanceledException) { SetOperationNotice("Проверка отменена."); }
        catch { SetOperationNotice("Не удалось выполнить проверку сервера."); }
        finally
        {
            ClearPendingProbes();
            probeCancellation.Dispose(); probeCancellation = null; IsProbing = false;
        }
    }
    private void ApplyProbeResult(ServerItemViewModel server, ServerProbeResult result)
    {
        var timedOut = !result.IsDeferred && result.Milliseconds is null && result.Message != "Не проверен";
        var previous = GetSnapshot(server.Profile);
        var key = ProfileKey(server.Profile);
        if (allProfiles.Any(profile => ProfileKey(profile) == key))
            probeSnapshots[key] = previous with
            {
                Text = result.Message,
                Milliseconds = result.Milliseconds,
                TimedOut = timedOut,
                Diagnostics = result.Diagnostics
            };
        pendingProbes.Remove(key);
        var visible = Profiles.FirstOrDefault(candidate => ProfileKey(candidate.Profile) == key) ?? server;
        visible.ProbeText = result.Message;
        visible.ProbeMilliseconds = result.Milliseconds;
        visible.ProbeTimedOut = timedOut;
        visible.IsProbeUnconfirmed = result.Diagnostics is { Method: "TCP", ProfileConfirmed: false };
        visible.IsProbePending = false;
        if (ConnectionState == VpnConnectionState.Connected && visible == activeProfile && probe is not IActiveTunnelProbe &&
            Platform.ReleaseTarget != ReleaseTarget.AndroidArm64)
            activeDelayMilliseconds = result.Milliseconds;
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
        OnPropertyChanged(nameof(HasConnectionError));
        if (visible == SelectedProfile)
        {
            OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay));
            OnPropertyChanged(nameof(DelayBrush));
            OnPropertyChanged(nameof(ConnectionHintBrush));
        }
    }

    private void ApplyPreciseProfileFailure(ServerItemViewModel server, ServerProbeResult result, ServerProbeMode mode)
    {
        var running = activeProfile ?? SelectedProfile;
        if (mode != ServerProbeMode.Https || result.IsDeferred || result.Milliseconds is not null ||
            ConnectionState != VpnConnectionState.Connected || running is null ||
            ProfileKey(running.Profile) != ProfileKey(server.Profile)) return;
        if (probe is not IActiveTunnelProbe) SetActiveProbeFailures(Math.Max(1, activeProbeFailures));
        StartActiveLatencyChecks();
    }

    private void SetActiveProbeFailures(int failures)
    {
        if (activeProbeFailures == failures) return;
        activeProbeFailures = failures;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PowerBorderBrush));
        OnPropertyChanged(nameof(PowerGlowBrush));
        OnPropertyChanged(nameof(ConnectionStateBrush));
        NotifyConnectionDelay();
    }

    private void NotifyConnectionDelay()
    {
        OnPropertyChanged(nameof(ConnectionHint));
        OnPropertyChanged(nameof(HasDisplayDelay));
        OnPropertyChanged(nameof(DelayBrush));
        OnPropertyChanged(nameof(ConnectionHintBrush));
    }

    private static string ProfileKey(ImportedProfile profile) => $"{profile.SourceId}\n{profile.Content}";

    private ProbeSnapshot GetSnapshot(ImportedProfile profile)
    {
        if (probeSnapshots.TryGetValue(ProfileKey(profile), out var snapshot)) return snapshot;
        var row = Profiles.FirstOrDefault(candidate => ProfileKey(candidate.Profile) == ProfileKey(profile));
        return row is null
            ? new ProbeSnapshot("Не проверен", null, false, null)
            : new ProbeSnapshot(row.ProbeText, row.ProbeMilliseconds, row.ProbeTimedOut, row.CountryCode);
    }

    private sealed record ProbeSnapshot(string Text, double? Milliseconds, bool TimedOut, string? CountryCode, ProbeDiagnostics? Diagnostics = null);

    private void ClearPendingProbes()
    {
        if (pendingProbes.Count == 0) return;
        pendingProbes.Clear();
        foreach (var row in Profiles) row.IsProbePending = false;
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
    }

    public void ToggleProbeMode()
    {
        UserSettings.Current.ServerProbeMode = UserSettings.Current.ServerProbeMode == ServerProbeMode.Fast ? ServerProbeMode.Https : ServerProbeMode.Fast;
        try { UserSettings.Current.Save(); Notice = "Тип проверки изменён."; }
        catch { Notice = "Не удалось сохранить тип проверки."; }
    }

    private async Task<ServerProbeResult[]> ProbeServersAsync(ServerItemViewModel[] servers, CancellationToken cancellationToken)
    {
        if (probe is null) return [];
        foreach (var server in servers)
        {
            pendingProbes.Add(ProfileKey(server.Profile));
            server.IsProbePending = true;
        }
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
        var mode = UserSettings.Current.ServerProbeMode;
        if (probe is IProgressiveServerProbe progressive)
        {
            var completed = new bool[servers.Length];
            var receivingProgress = true;
            void Apply(int index, ServerProbeResult result)
            {
                if (cancellationToken.IsCancellationRequested || (uint)index >= (uint)completed.Length || completed[index]) return;
                completed[index] = true;
                ApplyProbeResult(servers[index], result);
                ApplyPreciseProfileFailure(servers[index], result, mode);
            }
            var progress = new Progress<(int Index, ServerProbeResult Result)>(item =>
            {
                if (receivingProgress) Apply(item.Index, item.Result);
            });
            IReadOnlyList<ServerProbeResult> progressiveResults;
            try { progressiveResults = await progressive.ProbeManyAsync(servers.Select(server => server.Profile).ToArray(), progress, cancellationToken, mode); }
            finally { receivingProgress = false; }
            cancellationToken.ThrowIfCancellationRequested();
            if (progressiveResults.Count != servers.Length) throw new InvalidOperationException("Проверка вернула неполный результат.");
            for (var index = 0; index < progressiveResults.Count; index++) Apply(index, progressiveResults[index]);
            return progressiveResults.ToArray();
        }
        if (probe is IServerBatchProbe batchProbe)
        {
            var completed = new List<ServerProbeResult>(servers.Length);
            for (var offset = 0; offset < servers.Length; offset += 5)
            {
                var count = Math.Min(5, servers.Length - offset);
                var batchResults = await batchProbe.ProbeManyAsync(
                    servers.Skip(offset).Take(count).Select(server => server.Profile).ToArray(), cancellationToken, mode);
                cancellationToken.ThrowIfCancellationRequested();
                if (batchResults.Count != count) throw new InvalidOperationException("Проверка вернула неполный результат.");
                for (var index = 0; index < count; index++)
                {
                    ApplyProbeResult(servers[offset + index], batchResults[index]);
                    ApplyPreciseProfileFailure(servers[offset + index], batchResults[index], mode);
                }
                completed.AddRange(batchResults);
            }
            return completed.ToArray();
        }
        var results = new ServerProbeResult[servers.Length];
        using var concurrency = new SemaphoreSlim(mode == ServerProbeMode.Fast ? 8 : 2);
        await Task.WhenAll(servers.Select(async (server, index) =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                ServerProbeResult result;
                try { result = await probe.ProbeAsync(server.Profile, cancellationToken, mode); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    var platform = Platform.ReleaseTarget == ReleaseTarget.LinuxX64 ? "Linux"
                        : Platform.ReleaseTarget == ReleaseTarget.AndroidArm64 ? "Android" : "Windows";
                    result = ProbePolicy.Failure(error, "Служба проверки", mode == ServerProbeMode.Https ? "HTTPS" : "TCP", 0, platform);
                }
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = result;
                ApplyProbeResult(server, result);
                ApplyPreciseProfileFailure(server, result, mode);
            }
            finally { concurrency.Release(); }
        }));
        return results;
    }

    private void StartActiveLatencyChecks()
    {
        StopActiveLatencyChecks();
        if (probe is null) return;
        activeLatencyCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        _ = RunActiveLatencyLoopAsync(activeLatencyCancellation.Token);
    }

    private void StopActiveLatencyChecks()
    {
        activeLatencyCancellation?.Cancel();
        activeLatencyCancellation?.Dispose();
        activeLatencyCancellation = null;
    }

    private async Task RunActiveLatencyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var firstCheck = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = firstCheck ? TimeSpan.FromSeconds(1) : activeProbeFailures == 1
                    ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(1);
                await Task.Delay(delay, cancellationToken);
                while (IsBusy || IsConnecting || IsProbing || activeTunnelProbeRunning)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                firstCheck = false;
                await CheckActiveConnectionAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task CheckActiveConnectionAsync(CancellationToken cancellationToken)
    {
        var server = activeProfile ?? SelectedProfile;
        if (probe is null || server is null || IsProbing || activeTunnelProbeRunning ||
            ConnectionState != VpnConnectionState.Connected) return;
        var tunnel = probe as IActiveTunnelProbe;
        var connectedAt = engine?.Status.ConnectedAt;
        activeTunnelProbeRunning = true;
        try
        {
            var result = tunnel is not null ? await tunnel.ProbeActiveTunnelAsync(cancellationToken)
                : await probe.ProbeAsync(server.Profile, cancellationToken, ServerProbeMode.Https);
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectionState != VpnConnectionState.Connected || engine?.Status.ConnectedAt != connectedAt ||
                IsBusy || IsConnecting || IsProbing || (activeProfile ?? SelectedProfile)?.Profile.Content != server.Profile.Content) return;
            if (result.IsDeferred)
            {
                activeTransportFailures = 0;
                if (!activeTunnelConfirmed && result.Diagnostics is not null)
                {
                    SetActiveProbeFailures(0);
                    SetOperationNotice("Туннель запущен. " + result.Message, result.Diagnostics);
                }
                return;
            }
            if (tunnel is null) ApplyProbeResult(server, result);
            else activeTunnelConfirmed = result.Diagnostics?.ProfileConfirmed == true;
            activeDelayMilliseconds = result.Milliseconds;
            var failed = result.Milliseconds is null;
            activeTransportFailures = tunnel is not null && result.Diagnostics?.ErrorType is "Timeout" or "Connection"
                ? Math.Min(2, activeTransportFailures + 1) : 0;
            SetActiveProbeFailures(failed ? Math.Min(2, activeProbeFailures + 1) : 0);
            if (!failed && tunnel is not null)
            {
                healthRecoveryAttempted = false;
                healthFailoverAttempted = false;
            }
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PowerBorderBrush));
            OnPropertyChanged(nameof(PowerGlowBrush));
            OnPropertyChanged(nameof(ConnectionStateBrush));
            NotifyConnectionDelay();
            if (!failed && tunnel is null && engine?.Status is { State: VpnConnectionState.Connected, Message: { } networkMessage })
                SetOperationNotice(networkMessage);
            else
                SetOperationNotice(failed ? "Туннель запущен. " + result.Message
                    : tunnel is not null ? "VPN подключён." : "Туннель запущен. Профиль проверен.", result.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch { return; }
        finally { activeTunnelProbeRunning = false; }
        if (activeTransportFailures < 2 || engine is null ||
            IsBusy || IsConnecting || cancellationToken.IsCancellationRequested) return;
        if (!healthRecoveryAttempted)
        {
            healthRecoveryAttempted = true;
            await ReconnectSelectedProfileAsync(server, restartCurrent: true);
            return;
        }
        if (healthFailoverAttempted) return;
        var alternative = IsLowestMode ? Profiles.Where(candidate => candidate.Profile.SourceId == server.Profile.SourceId &&
                candidate.Profile.Content != server.Profile.Content && IsUsableProfile(candidate) &&
                GetSnapshot(candidate.Profile).Diagnostics?.ProfileConfirmed == true)
            .OrderBy(candidate => candidate.ProbeMilliseconds).ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        if (alternative is null) return;
        healthFailoverAttempted = true;
        SelectedProfile = alternative;
        await ReconnectSelectedProfileAsync(alternative);
    }

    private bool IsUsableProfile(ServerItemViewModel server) => server.ProbeMilliseconds is not null
        && server.Profile.Usage?.IsExhausted(DateTimeOffset.UtcNow) != true
        && (Platform.ReleaseTarget != ReleaseTarget.AndroidArm64
            || probeSnapshots.TryGetValue(ProfileKey(server.Profile), out var snapshot) && snapshot.Diagnostics?.ProfileConfirmed == true);

    private void SortProfilesByProbe()
    {
        var selected = SelectedProfile;
        var ordered = Profiles
            .OrderBy(server => server.ProbeMilliseconds is not null ? 0 : server.ProbeTimedOut ? 2 : 1)
            .ThenBy(server => server.ProbeMilliseconds ?? double.MaxValue)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Profiles.Clear();
        foreach (var server in ordered) Profiles.Add(server);
        SelectedProfile = selected;
    }
    private async Task<bool> SelectLowestProfileAsync()
    {
        if (ConnectionState == VpnConnectionState.Connected && (activeProbeFailures > 0 || healthRecoveryAttempted)) return false;
        var best = Profiles.Where(IsUsableProfile)
            .OrderBy(server => server.ProbeMilliseconds).ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (best is null || best == SelectedProfile) return false;
        SelectedProfile = best;
        Notice = $"Lowest выбрал сервер {best.Name}.";
        if (ConnectionState == VpnConnectionState.Connected && best != activeProfile)
            await ReconnectSelectedProfileSafelyAsync(best);
        return true;
    }
    internal async Task DisconnectForUpdateAsync()
    {
        CancelProbes();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await CancelConnectionAsync().WaitAsync(timeout.Token);
        if (engine is null) return;
        await engine.DisconnectAsync(timeout.Token).WaitAsync(timeout.Token);
        if (engine.Status.State is not (VpnConnectionState.Disconnected or VpnConnectionState.Error))
            throw new UpdateInstallException("VPN ещё отключается. Повторите обновление после отключения.");
    }

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
        lowestCancellation?.Cancel();
        activeLatencyCancellation?.Cancel();
        if (engine is null) return;
        await engine.DisconnectAsync();
        engine.StatusChanged -= EngineStatusChanged;
        await engine.DisposeAsync();
    }
}
