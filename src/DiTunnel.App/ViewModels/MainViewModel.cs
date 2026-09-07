using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

public sealed record ProfileGroup(string Id, string Name)
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
    private readonly List<ImportedProfile> allProfiles = [];
    private readonly IProfileVpnEngine? engine;
    private readonly IProfileStore profileStore;
    private bool storageAvailable = true;
    private readonly IServerProbe? probe;
    private readonly IServerCountryResolver? countryResolver;
    private ServerItemViewModel? activeProfile;
    private CancellationTokenSource? connectionCancellation;
    private CancellationTokenSource? probeCancellation;
    private CancellationTokenSource? lowestCancellation;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly DispatcherTimer mapLightsTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Random mapLightsRandom = new();
    private bool mapLightsFading;
    private int mapLightsFadeCursor;
    private int mapLightsChangeTick;
    [ObservableProperty] private string subscriptionStatus = "";
    [ObservableProperty] private string refreshToast = "";
    [ObservableProperty] private bool isRefreshToastVisible;

    public async Task RefreshSubscriptionsAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null)
    {
        if (!CanImport || !storageAvailable) return;
        var sources = allProfiles.Where(p => p.SourceUrl is not null).GroupBy(p => p.SourceId).Select(g => g.First()).ToArray();
        if (sources.Length == 0) return;
        download ??= new ProfileImporter().ImportAsync;
        IsBusy = true;
        SubscriptionStatus = "Обновляем подписки…";
        var updated = 0; var failed = 0;
        var selectedContent = SelectedProfile?.Profile.Content;
        try
        {
            foreach (var source in sources)
            {
                try
                {
                    var fresh = await download(source.SourceUrl!, lifetimeCancellation.Token);
                    lifetimeCancellation.Token.ThrowIfCancellationRequested();
                    if (fresh.Count == 0) throw new FormatException();
                    var replacement = fresh.DistinctBy(p => p.Content).Select(p => p with { SourceId = source.SourceId, SourceName = source.SourceName, SourceUrl = source.SourceUrl });
                    var next = allProfiles.Where(p => p.SourceId != source.SourceId).Concat(replacement).ToArray();
                    profileStore.Save(next);
                    allProfiles.Clear(); allProfiles.AddRange(next);
                    updated++;
                }
                catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { break; }
                catch { failed++; }
            }
            RebuildGroups();
            SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Content == selectedContent) ?? SelectedProfile;
            SubscriptionStatus = failed == 0 ? $"Подписки обновлены: {updated} · {DateTime.Now:HH:mm}" : $"Обновлено: {updated}. Не удалось обновить: {failed}. Сохранены прежние серверы.";
            RefreshToast = failed == 0 ? "Профиль успешно обновлён" : "Не удалось обновить профиль";
            IsRefreshToastVisible = true;
            _ = HideRefreshToastAsync();
        }
        finally { IsBusy = false; }
    }

    private async Task HideRefreshToastAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), lifetimeCancellation.Token);
            IsRefreshToastVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(string.Empty);
        foreach (var row in Profiles) row.RefreshLanguage();
    }
    [ObservableProperty] private bool isProbing;
    public ObservableCollection<ServerItemViewModel> Profiles { get; } = [];
    public ObservableCollection<ProfileGroup> Groups { get; } = [];
    [ObservableProperty] private ProfileGroup? selectedGroup;
    [ObservableProperty] private ServerItemViewModel? selectedProfile;
    [ObservableProperty] private bool isImportOpen;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private VpnConnectionState connectionState;
    [ObservableProperty] private string importText = "";
    [ObservableProperty] private string importName = "";
    [ObservableProperty] private string importMessage = "";
    [ObservableProperty] private string notice = "Добавьте подписку или конфигурацию, чтобы выбрать сервер.";
    public string StatusText => ConnectionState switch
    {
        VpnConnectionState.Connecting => "Подключаем VPN…",
        VpnConnectionState.Connected => "VPN подключён",
        VpnConnectionState.Disconnecting => "Отключаем VPN…",
        VpnConnectionState.Error => "Не удалось подключиться",
        _ => "VPN отключён"
    };
    public string PowerText => IsConnecting && ConnectionState != VpnConnectionState.Disconnecting ? "Отменить" : ConnectionState == VpnConnectionState.Connected ? "Отключить" : "Подключить";
    public string CopyErrorText => "Копировать";
    public bool HasConnectionError => ConnectionState == VpnConnectionState.Error && !string.IsNullOrWhiteSpace(Notice);
    public string ConnectionHint => ConnectionState == VpnConnectionState.Connected && engine?.Status.DelayMilliseconds is { } delay
        ? $"⌁  {delay:0} мс"
        : engine?.RequiresAdministrator == true
            ? "Для TUN запустите приложение от администратора"
            : "Windows TUN · Xray-core";
    public string SelectedName => SelectedProfile?.Name ?? L.T("Сервер не выбран");
    public string SelectedSummary => SelectedProfile?.Summary ?? "Импортируйте свою первую подписку";
    public string SubscriptionLimits
    {
        get
        {
            var usage = SelectedProfile?.Profile.Usage;
            if (usage is null) return "";
            var parts = new List<string>();
            if (usage.RemainingBytes is { } remaining) parts.Add($"{remaining / 1073741824m:0.##} ГБ осталось");
            if (usage.RemainingDays(DateTimeOffset.Now) is { } days) parts.Add($"{days} дн. осталось");
            return string.Join(" · ", parts);
        }
    }
    public string ProfileCount => $"Серверов: {Profiles.Count}";
    public string AppVersion => UserSettings.Version;
    public IBrush PowerBrush => new SolidColorBrush(Color.Parse("#292039"));
    public IBrush PowerBorderBrush => ConnectionState switch { VpnConnectionState.Connected => new SolidColorBrush(Color.Parse("#55D784")), VpnConnectionState.Error => new SolidColorBrush(Color.Parse("#F06470")), _ => new SolidColorBrush(Color.Parse("#A58AFF")) };
    public IBrush PowerGlowBrush => ConnectionState switch { VpnConnectionState.Connected => new SolidColorBrush(Color.Parse("#3830C86D")), VpnConnectionState.Error => new SolidColorBrush(Color.Parse("#40D94A58")), _ => new SolidColorBrush(Color.Parse("#18000000")) };
    public IBrush ConnectionStateBrush => ConnectionState switch { VpnConnectionState.Connected => new SolidColorBrush(Color.Parse("#66E491")), VpnConnectionState.Error => new SolidColorBrush(Color.Parse("#FF7783")), _ => new SolidColorBrush(Color.Parse("#C9B9FF")) };
    public IBrush DelayBrush => engine?.Status.DelayMilliseconds is { } delay && delay >= 1000 ? new SolidColorBrush(Color.Parse("#E8C968")) : new SolidColorBrush(Color.Parse("#66E491"));
    public IBrush ConnectionHintBrush => ConnectionState == VpnConnectionState.Connected
        ? DelayBrush
        : new SolidColorBrush(Color.Parse("#B7ABC8"));
    [ObservableProperty] private bool mapLightsVisible;
    public ObservableCollection<MapLightViewModel> MapLights { get; } = CreateMapLights();
    public Avalonia.Media.Imaging.Bitmap? SelectedFlag => SelectedProfile?.FlagImage;
    public bool HasSelectedFlag => SelectedFlag is not null;
    public bool CanImport => !IsBusy && !IsConnecting && !IsProbing && ConnectionState != VpnConnectionState.Connected;
    public bool CanSelectProfile => !IsBusy && !IsConnecting && !IsProbing;
    public bool CanConnect => !IsBusy && !IsProbing && ConnectionState != VpnConnectionState.Disconnecting;
    // A latency test never changes the selected VPN connection, so it remains safe while TUN is active.
    public bool CanProbe => !IsBusy && !IsConnecting && !IsProbing && probe is not null && SelectedProfile is not null;
    public bool CanProbeAll => !IsBusy && !IsConnecting && !IsProbing && probe is not null && Profiles.Count != 0;
    public bool IsLowestMode => UserSettings.Current.LowestMode;

    public MainViewModel() : this(null) { }
    public MainViewModel(IProfileVpnEngine? engine, IProfileStore? store = null, IServerProbe? probe = null, IServerCountryResolver? countryResolver = null)
    {
        this.engine = engine;
        this.probe = probe;
        this.countryResolver = countryResolver;
        profileStore = store ?? new ProfileStorage();
        if (engine is not null) engine.StatusChanged += EngineStatusChanged;
        mapLightsTimer.Tick += (_, _) => AdvanceMapLights();
        try
        {
            allProfiles.AddRange(profileStore.Load());
            RebuildGroups();
            if (SelectedProfile is not null) Notice = "Выберите профиль и сервер для подключения.";
        }
        catch { Notice = "Не удалось прочитать сохранённые профили. Исходный файл не изменён."; storageAvailable = false; }
        if (IsLowestMode) StartLowestMode(checkImmediately: false);
    }

    private void EngineStatusChanged(object? sender, VpnStatus status) => Dispatcher.UIThread.Post(() =>
    {
        if (engine?.Status != status) return;
        ConnectionState = status.State;
        if (status.State == VpnConnectionState.Connected && status.DelayMilliseconds is { } delay && SelectedProfile is { } server)
        {
            activeProfile = server;
            server.ProbeText = $"HTTPS · {delay:0} мс";
        }
        else if (status.State == VpnConnectionState.Disconnected) activeProfile = null;
        if (status.Message is not null) Notice = status.Message;
    });
    partial void OnConnectionStateChanged(VpnConnectionState value)
    {
        if (value == VpnConnectionState.Connected)
        {
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
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(CopyErrorText)); OnPropertyChanged(nameof(HasConnectionError)); OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(DelayBrush)); OnPropertyChanged(nameof(ConnectionHintBrush)); OnPropertyChanged(nameof(PowerBrush)); OnPropertyChanged(nameof(PowerBorderBrush)); OnPropertyChanged(nameof(PowerGlowBrush)); OnPropertyChanged(nameof(ConnectionStateBrush)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
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
        new(220, 250), new(260, 235), new(290, 275), new(320, 305),
        new(425, 490), new(440, 540), new(430, 590),
        new(730, 200), new(780, 190), new(830, 205), new(880, 210), new(930, 215),
        new(1180, 250), new(1240, 270), new(1300, 280),
        new(835, 400), new(860, 450), new(1320, 500), new(1370, 535), new(1450, 635)
    ];
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); OnPropertyChanged(nameof(CanConnect)); }
    partial void OnIsProbingChanged(bool value) { OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); }
    partial void OnIsConnectingChanged(bool value) { OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanSelectProfile)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll)); OnPropertyChanged(nameof(CanConnect)); }
    partial void OnSelectedGroupChanged(ProfileGroup? value)
    {
        Profiles.Clear();
        foreach (var profile in allProfiles.Where(p => p.SourceId == value?.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Profiles.Add(new ServerItemViewModel(profile));
        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(ProfileCount)); OnPropertyChanged(nameof(CanProbeAll));
        _ = ResolveCountriesAsync(Profiles.ToArray());
    }
    private async Task ResolveCountriesAsync(ServerItemViewModel[] rows)
    {
        if (countryResolver is null) return;
        foreach (var row in rows)
        {
            if (lifetimeCancellation.IsCancellationRequested) return;
            string? code;
            try { code = await countryResolver.ResolveAsync(row.Profile, lifetimeCancellation.Token); }
            catch { continue; }
            Dispatcher.UIThread.Post(() =>
            {
                if (!Profiles.Contains(row)) return;
                row.CountryCode = code;
                if (SelectedProfile == row) { OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag)); }
            });
        }
    }
    partial void OnSelectedProfileChanged(ServerItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag));
        OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(SubscriptionLimits)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
        if (value is not null && value != activeProfile && (ConnectionState == VpnConnectionState.Connected || ConnectionState == VpnConnectionState.Error))
        {
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

    private async Task ReconnectSelectedProfileAsync(ServerItemViewModel target)
    {
        if (engine is null || IsConnecting || (ConnectionState != VpnConnectionState.Connected && ConnectionState != VpnConnectionState.Error) || target == activeProfile) return;
        IsConnecting = true;
        var cancellation = connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        Notice = $"Переключаемся на сервер {target.Name}…";
        try
        {
            if (ConnectionState == VpnConnectionState.Connected) await engine.DisconnectAsync(cancellation.Token);
            if (SelectedProfile != target || cancellation.IsCancellationRequested)
            {
                return;
            }
            await engine.ConnectAsync(target.Profile, cancellation.Token);
            activeProfile = target;
        }
        catch (OperationCanceledException) { Notice = "Переключение сервера отменено."; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException or TimeoutException) { Notice = e.Message; }
        catch { Notice = "Не удалось переключить сервер. Предыдущее подключение отключено; выберите сервер и подключитесь снова."; }
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
        preferred ??= SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in allProfiles.GroupBy(p => p.SourceId)) Groups.Add(new(group.Key, group.First().SourceName));
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == preferred) ?? Groups.FirstOrDefault();
        // A record-equal selection does not raise a property change after importing into an existing group.
        OnSelectedGroupChanged(SelectedGroup);
    }
    [RelayCommand] private void OpenImport() { if (!CanImport) return; ImportMessage = ""; IsImportOpen = true; }
    [RelayCommand] private void CloseImport() { if (!IsBusy) { IsImportOpen = false; ImportText = ""; ImportName = ""; } }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task ConnectAsync()
    {
        if (IsConnecting) { connectionCancellation?.Cancel(); Notice = "Отменяем подключение и восстанавливаем сеть…"; return; }
        if (!CanConnect) return;
        if (engine is null) { Notice = "Сетевой движок недоступен на этой платформе."; return; }
        IsConnecting = true;
        connectionCancellation = new CancellationTokenSource();
        try
        {
            if (ConnectionState == VpnConnectionState.Connected) await engine.DisconnectAsync();
            else if (SelectedProfile is null) Notice = "Сначала выберите сервер.";
            else
            {
                await engine.ConnectAsync(SelectedProfile.Profile, connectionCancellation.Token);
                if (engine.Status.State == VpnConnectionState.Connected) activeProfile = SelectedProfile;
            }
        }
        catch (OperationCanceledException) { Notice = "Подключение отменено."; }
        catch (TimeoutException e) { Notice = e.Message; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException) { Notice = e.Message; }
        catch { Notice = "Не удалось изменить состояние VPN. Сетевые изменения отменены; проверьте права и доступность сервера."; }
        finally { ConnectionState = engine.Status.State; IsConnecting = false; connectionCancellation.Dispose(); connectionCancellation = null; }
    }
    [RelayCommand] private void RemoveProfile()
    {
        if (SelectedProfile is null || !CanImport) return;
        RemoveWhere(p => p == SelectedProfile.Profile, "Сервер удалён.");
    }
    [RelayCommand] private void RemoveGroup()
    {
        if (SelectedGroup is null || !CanImport) return;
        RemoveWhere(p => p.SourceId == SelectedGroup.Id, "Профиль и все его серверы удалены.");
    }
    private void RemoveWhere(Func<ImportedProfile, bool> predicate, string message)
    {
        var remaining = allProfiles.Where(p => !predicate(p)).ToArray();
        try
        {
            if (!storageAvailable) throw new IOException();
            profileStore.Save(remaining);
            allProfiles.Clear(); allProfiles.AddRange(remaining); RebuildGroups(); Notice = message;
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
            var imported = await new ProfileImporter().ImportAsync(input);
            var sourceUrl = Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? input : null;
            var existing = sourceUrl is null ? null : allProfiles.FirstOrDefault(p => p.SourceUrl == sourceUrl);
            var sourceId = existing?.SourceId ?? Guid.NewGuid().ToString("N");
            var sourceName = string.IsNullOrWhiteSpace(ImportName) ? existing?.SourceName ?? (sourceUrl is null ? "Ручной профиль" : "Подписка") + $" {Groups.Count + 1}" : ImportName.Trim();
            var added = imported.Where(p => !allProfiles.Any(e => e.SourceId == sourceId && e.Content == p.Content))
                .Select(p => p with { SourceId = sourceId, SourceName = sourceName, SourceUrl = sourceUrl }).ToArray();
            if (!storageAvailable) { ImportMessage = "Хранилище недоступно. Сохранённый файл защищён от перезаписи."; return; }
            profileStore.Save(allProfiles.Concat(added));
            allProfiles.AddRange(added); RebuildGroups(sourceId);
            Notice = added.Length == 0 ? "Эти серверы уже есть в подписке." : $"Добавлено серверов: {added.Length}.";
            ImportText = ""; ImportName = ""; IsImportOpen = false;
        }
        catch (FormatException e) { ImportMessage = e.Message; }
        catch (HttpRequestException) { ImportMessage = "Не удалось загрузить подписку. Проверьте адрес, доступность сервера и сертификат HTTPS."; }
        catch (OperationCanceledException) { ImportMessage = "Сервер не ответил вовремя. Попробуйте ещё раз."; }
        catch { ImportMessage = "Не удалось импортировать или сохранить профили. Существующие профили сохранены."; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private Task ProbeSelectedAsync() => RunProbesAsync(false);
    [RelayCommand] private Task ProbeAllAsync() => RunProbesAsync(true);
    [RelayCommand] private void CancelProbes() => probeCancellation?.Cancel();

    public void SetLowestMode(bool enabled)
    {
        UserSettings.Current.LowestMode = enabled;
        try { UserSettings.Current.Save(); } catch { Notice = "Не удалось сохранить настройки режима lowest."; return; }
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

    private async Task<bool> CheckLowestAsync(CancellationToken cancellationToken)
    {
        if (!CanProbeAll || probe is null) return false;
        var servers = Profiles.ToArray();
        IsProbing = true;
        try
        {
            var values = new List<(ServerItemViewModel Server, double Delay)>();
            foreach (var server in servers)
            {
                server.ProbeText = "Проверяем…";
                var result = await probe.ProbeAsync(server.Profile, cancellationToken);
                ApplyProbeResult(server, result);
                if (result.Milliseconds is { } delay) values.Add((server, delay));
            }
            SortProfilesByProbe();
            return SelectLowestProfile();
        }
        catch (OperationCanceledException) { throw; }
        catch { Notice = "Lowest: не удалось проверить серверы."; return false; }
        finally { IsProbing = false; }
    }

    private async Task RunProbesAsync(bool all)
    {
        if ((!all && !CanProbe) || (all && !CanProbeAll) || probe is null) return;
        var servers = all ? Profiles.ToArray() : [SelectedProfile!];
        IsProbing = true;
        probeCancellation = new CancellationTokenSource();
        try
        {
            foreach (var server in servers)
            {
                probeCancellation.Token.ThrowIfCancellationRequested();
                server.ProbeText = "Проверяем…";
                Notice = $"Проверка {Array.IndexOf(servers, server) + 1} из {servers.Length}: {server.Name}";
                var result = await probe.ProbeAsync(server.Profile, probeCancellation.Token);
                ApplyProbeResult(server, result);
            }
            SortProfilesByProbe();
            var changed = all && IsLowestMode && SelectLowestProfile();
            if (!changed) Notice = "Проверка завершена. Показано время HTTPS-запроса через сервер; это не ICMP-пинг. Маршруты Windows не менялись.";
        }
        catch (OperationCanceledException) { Notice = "Проверка отменена."; }
        catch { Notice = "Не удалось выполнить проверку сервера."; }
        finally
        {
            foreach (var server in servers.Where(s => s.ProbeText == "Проверяем…")) server.ProbeText = "Не проверен";
            probeCancellation.Dispose(); probeCancellation = null; IsProbing = false;
        }
    }
    private static void ApplyProbeResult(ServerItemViewModel server, ServerProbeResult result)
    {
        server.ProbeText = result.Message;
        server.ProbeMilliseconds = result.Milliseconds;
        server.ProbeTimedOut = result.Milliseconds is null && result.Message != "Не проверен";
    }
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
    private bool SelectLowestProfile()
    {
        var best = Profiles.Where(server => server.ProbeMilliseconds is not null)
            .OrderBy(server => server.ProbeMilliseconds).ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (best is null || best == SelectedProfile) return false;
        SelectedProfile = best;
        Notice = $"Lowest выбрал сервер {best.Name}.";
        return true;
    }
    public async Task PrepareToCloseAsync(TimeSpan timeout)
    {
        try { await ShutdownAsync().WaitAsync(timeout); }
        catch { Notice = "Окно закрывается; независимый сетевой модуль продолжит восстановление сети."; }
    }

    public async Task ShutdownAsync()
    {
        lifetimeCancellation.Cancel();
        connectionCancellation?.Cancel();
        probeCancellation?.Cancel();
        lowestCancellation?.Cancel();
        if (engine is null) return;
        await engine.DisconnectAsync();
        engine.StatusChanged -= EngineStatusChanged;
        await engine.DisposeAsync();
    }
}
