# ADR 0003: общий интерфейс и платформенная интеграция Linux

- Статус: предложено к реализации по результатам просмотра исходников
- Дата: 7 октября 2026 года
- Исходная версия: Di-Tunnel 0.5.19
- Первая цель: Ubuntu 24.04 LTS Desktop amd64
- Связанный документ: [план Linux и испытаний](../linux-development-plan.md)
- Первый выполненный этап: [общий UI, Linux entry и защищённое хранилище](../linux-first-stage.md). Привилегированная служба и VPN остаются проектируемыми компонентами.

## Решение

Linux подключается к существующему приложению как третья платформа. Сохраняем общий Avalonia UI, ViewModels, модели профилей, импорт, подписки и правила Xray. Создаём Linux точку входа, платформенные adapters и привилегированный сетевой host. Общий код также требует небольшого рефакторинга: в нём остаются предположения о Windows/Android.

Ниже описана целевая архитектура. Анализ предшествовал реализации; актуальное состояние первой оболочки и результаты запусков приведены в связанном документе первого этапа. Наличие Linux UI не подтверждает работу системного VPN.

## Основания в текущем коде

| Область | Подтверждено исходниками | Решение |
| --- | --- | --- |
| UI | `DiTunnel.App` на `net10.0`: MainView/MainWindow, ViewModels, карта, локализация, themes и dialogs | Один проект/XAML для трёх платформ; настройки отражают capabilities |
| Composition root | Windows Program и AndroidApp задают App.CreateMainViewModel; Android явно передаёт engine/store/probe/country/apps | Linux подключает adapters тем же способом |
| Core | Импорт, AWG parsing, профили, статусы, split policy и reconnect schedule | Переиспользовать; Windows идентификаторы отделить от общих намерений |
| Xray infrastructure | `net10.0`, converters, GeoIpDatabase, process manager и SOCKS/HTTP probes | Переиспользовать с платформенными network/lifecycle options |
| Windows network | UI facade → authenticated pipe/UAC → broker/engine/tunnel host | Сохранить границу UI/host, заменить transport и операции ОС |
| Android | Общий App с Keystore/VpnService/libXray и native services | Переиспользовать схему adapters; Android API остаётся платформенным |
| AWG | Общие Go bridge/netstack/SOCKS, отдельные Windows/Android binding/lifecycle | Общий bridge с Linux glue; новый protocol stack не нужен |

Точки проверки: [App](../../src/DiTunnel.App/App.axaml.cs), [MainViewModel](../../src/DiTunnel.App/ViewModels/MainViewModel.cs), [Windows entry](../../src/DiTunnel.Desktop/Program.cs), [Android entry](../../src/DiTunnel.Android/AndroidApp.cs), [Windows client](../../src/DiTunnel.Platform.Windows/WindowsNetworkClient.cs), [host protocol](../../src/DiTunnel.Platform.Windows/Network/NetworkProtocol.cs), [Xray converter](../../src/DiTunnel.Infrastructure.Xray/XrayProfileConverter.cs).

## Проекты и зависимости

```text
DiTunnel.Desktop ─┐
DiTunnel.Android ┼──> DiTunnel.App ──> DiTunnel.Core
DiTunnel.Linux ──┘        общий UI
      │
      └──> DiTunnel.Platform.Linux ──> DiTunnel.Core
                        │
                        └──> DiTunnel.Infrastructure.Xray ──> DiTunnel.Core

DiTunnel.NetworkHost.Linux ──> Platform.Linux + Infrastructure.Xray
      systemd service, без зависимости от App/Avalonia
```

- **DiTunnel.Linux**, `net10.0`: Avalonia Desktop entry, настройка зависимостей, single instance/активация окна и Linux updater. Updater реализует существующий App `IUpdateInstaller` и находится в entry project, чтобы platform library не зависела от App.
- **DiTunnel.Platform.Linux**, `net10.0`: Core adapters; каталоги Client, Network, Runtime, Storage, Desktop. Без ссылки на Avalonia/App; библиотека используется UI и host, соответствующие объекты создаёт их composition root.
- **DiTunnel.NetworkHost.Linux**, `net10.0`: консольная точка входа systemd service, D-Bus API, polkit authorization и сборка сетевых компонентов.

Windows target/installer сначала остаются в текущих проектах. Не копируем App в Linux и не превращаем Windows Desktop в набор условных platform references. Отдельный Desktop.Common пока не нужен: общая desktop оболочка уже находится в App. Общий host coordinator извлекается после Linux proof, когда понятны реальные совпадения; WindowsTunnelHost целиком не переносится.

## Контракты и настройка общего UI

Основной рабочий контракт — существующий `IProfileVpnEngine` (профиль, switch, статус, disposal), а не более узкий `IVpnEngine`. Также переиспользуем `IServerProbe`, `IServerCountryResolver`, `IInstalledApplicationProvider`, `IProfileStore`, `INetworkRecoveryEngine`. LinuxNetworkClient реализует engine/probe/recovery, как WindowsNetworkClient.

Добавить небольшой типизированный набор зависимостей/платформенных сведений, условно `AppPlatformServices` и `PlatformDescriptor`. Передавать при запуске/в конструктор ViewModel, без поиска сервисов внутри ViewModels. Существующие App.CreateMainViewModel/UpdateInstaller/clipboard/QR hooks допустимы как переходный механизм; большой DI-контейнер для Linux не требуется.

Разделить:

1. **VPN capabilities в Core**: TUN, process/domain rules, собственный kill switch, системный lockdown, recovery. Host сообщает доступность и причины отказа.
2. **Presentation capabilities в App**: desktop/mobile layout, application picker, QR input, autostart и реальная доступность tray/notifications.
3. **Platform/release descriptor**: названия backend/ОС, защита профилей, версия, RID и формат update asset.

Supported — возможность, Ready — наличие runtime/службы/прав, Active — фактическое состояние защиты. Галочка kill switch не является доказательством Active. Отсутствие host, keyring или polkit не мешает открыть UI и получить понятную ошибку.

### Рефакторинг общих файлов

| Текущий участок | Требуемое изменение |
| --- | --- |
| App ProfileStorage: DPAPI; MainViewModel default store | Перенести реализацию в Windows platform, явно передать из Windows entry. Сохранить формат/путь Windows; Linux получает LinuxProfileStore. Designer/tests используют явные тестовые зависимости |
| MainViewModel: Windows runtime/storage descriptions; `!IsAndroid` для kill switch | Descriptor для подписей, capability для возможности, engine/host для Active |
| Dialogs: только Windows/Android apps, `.exe`, WFP text | Общий экран, platform provider/picker и описание механизма защиты |
| UserSettings/ConnectionPolicy StartWithWindows | StartAtLogin с миграцией старого JSON поля; AutoConnect остаётся независимым |
| UserSettings fingerprint/dedup и Dialogs OrdinalIgnoreCase | Домены без регистра; Linux executable paths — Ordinal. Не смешивать `/opt/A/app` и `/opt/a/app`; сохранить сравнение Windows/Android |
| Core KillSwitchRulePlan InterfaceLuid и WFP application identity | Windows детали в adapter, общие намерения/статус в Core. Linux policy использует ifindex/name/marks/nft objects |
| ReleaseChecker bool android; версия только Windows/Android | Явный platform/architecture/package target и LinuxVersion; Linux не выбирает `.exe`/APK |
| MainWindow разрешает Hide при tray != null | Проверять реально доступный tray host; в GNOME нельзя терять единственное окно |
| UserSettings.Current/DataDirectory и Diagnostics paths | Platform paths настраиваются до первого Load; Linux использует XDG, существующие Windows/Android пути сохраняются |

Синхронный `IProfileStore.Load/Save` пока можно сохранить. Secret Service unlock выполняется асинхронно до чтения профилей, с состоянием initialization и повторной разблокировкой; не блокировать UI через `.Result`. В MainViewModel уже есть `storageAvailable`, защищающий файл от перезаписи после Load error: сохранить этот guard.

Условные настройки мобильной компоновки допустимы в общем UI. DPAPI/WFP вызовы, правила файлов ОС и сетевые операции относятся к adapters.

## Компоненты Linux platform

| Новый компонент | Ответственность | Общая основа |
| --- | --- | --- |
| LinuxNetworkClient | Engine/probe/recovery через IPC, status и реконнект IPC | Core contracts и общие UI commands |
| LinuxVpnEngine | Connect/switch/disconnect/reconnect внутри службы | Converters, policies, reconnect schedule |
| LinuxTunnelHost | Сессия runtime и сетевые leases, cleanup без GUI | Идеи этапов/ownership; операции ОС новые |
| LinuxNetworkController | TUN addresses, netlink routes/policy rules, физический egress | Нейтральный план; WindowsNetworkApi заменяется |
| LinuxDnsController | Per-link DNS/domains/default-route systemd-resolved | Общая DNS политика; Windows NRPT заменяется |
| LinuxKillSwitchController | Своя nftables inet table, IPv4/IPv6 и узкие exceptions | Общие статус/намерения защиты |
| LinuxPhysicalNetworkMonitor | Link/address/route, NetworkManager, sleep/resume | Recovery reasons/schedule |
| Linux runtime/lifecycle adapter | Pinned paths, runtime dirs, signals/process groups | XrayOptions/manager/probe helpers |
| LinuxAmneziaWgRuntime | Тот же userspace bridge с физическим egress | AWG converter, SOCKS readiness/credentials |
| LinuxProfileStore | Зашифрованный пользовательский файл и Secret Service key | IProfileStore/serializer и storage guard |
| LinuxInstalledApplicationProvider | XDG desktop entries и фактические executable paths | InstalledApplication и общий выбор |
| Desktop integration | XDG autostart, notifications, single instance | MainWindow/tray/menu и UI state |

GeoIpDatabase уже общий. Country resolvers Windows/Android похожи: общую aggregation/cache логику можно извлечь с adapters загрузки базы и DNS; не добавлять третью копию без необходимости. DNS lookup обязан учитывать действующую защиту.

Для первого Linux варианта InstalledApplication.Id остаётся routing value — реальным абсолютным executable path. Desktop entry ID/название — отдельные metadata provider. Exec wrapper или Flatpak/Snap не обязательно совпадает с сетевым процессом: показывать ограничение/ручной выбор, не выдавать непроверенный matcher за рабочий.

## Привилегированная служба и IPC

Основной транспорт — **system D-Bus**. DTO/request semantics можно вынести из Windows internal NetworkProtocol в Core. Length-prefixed framing для D-Bus не требуется: bounded typed payload с явной версией схемы. Windows wire format сначала сохраняется.

Предлагаемые операции: GetCapabilities, GetState, Connect, Switch, Disconnect, Probe, Cancel, RestoreOwnedNetwork; события status/protection. Профили/options ограничены схемой; произвольные shell strings, executable paths и root Xray JSON не принимаются.

Служба проверяет D-Bus sender, polkit и владельца сессии. На host один активный системный VPN; другой пользователь не перезаписывает его. Status signals не публикуют profile Content/SourceUrl. Для повторного открытия UI нужен идентификатор профиля, сопоставляемый с пользовательским store. Источник авторизации: [polkit](https://polkit.pages.freedesktop.org/polkit/polkit.8.html).

Connection operations сериализуются. Cancel подтверждается после rollback; session/generation ID защищает от устаревших событий. Probes имеют ограниченные отдельные runtime leases и не меняют active selection/TUN. При уже активном kill switch проверке нужен разрешённый egress от host, а не произвольный UI process.

UI не запускается через sudo. Пакет устанавливает root-owned runtime/host. Минимальные права дочерних процессов уточняются экспериментом: NET_ADMIN для TUN не гарантирует чтение чужих `/proc/<pid>/fd`. Root host не зависит от Avalonia и не исполняет пользовательские binaries/hooks.

### Жизненный цикл

- Явные Disconnect/Exit: отмена операций, остановка runtime, удаление собственных DNS/routes/filters; сохраняем общий MainViewModel.ShutdownAsync сценарий.
- Hide: UI и VPN продолжают работу.
- Потеря UI/IPC: host завершает транзакцию самостоятельно; с kill switch сохраняет fail-closed защиту до подтверждённого восстановления/авторизованного Disconnect. Обрыв IPC не равен снятию защиты.
- Crash Xray/AWG: Reconnecting/Error и отсутствие прямого fallback.
- Crash host: собственные filters/journal обеспечивают recovery service restart; UI не сообщает неподтверждённый cleanup.
- Reboot/pre-login Always-on: последующий этап с отдельными boot ordering и secret storage решениями.

## Общее ядро Xray/AWG и Linux параметры

```text
трафик приложений → Linux TUN → Xray routing/adblock
                                ├─ protocol outbound → сервер
                                └─ authenticated SOCKS → AWG bridge → сервер
```

Pinned Xray v26.3.27 уже содержит Linux TUN и process lookup через `/proc`. Его TUN config знает только name/MTU/userLevel: адреса, маршруты и DNS задаёт наш host. Новые auto-routing поля из документации других версий не считаются доступными. Источники: [Linux TUN](https://github.com/XTLS/Xray-core/blob/v26.3.27/proxy/tun/tun_linux.go), [TUN config](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/tun.go), [process lookup](https://github.com/XTLS/Xray-core/blob/v26.3.27/common/net/find_process_linux.go).

Общий builder получает типизированные platform network options: TUN name/MTU, transport mark/interface, families и direct policy. Pinned source поддерживает sockopt mark/interface; применение к нашим transports требует VM proof. VPN transport bypass и намеренный direct split outbound разделяются. Источник: [SocketConfig v26.3.27](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/transport_internet.go).

Выявленные точки изменения:

- XrayProfileConfiguration.Build: фиксированный MTU 1400/autoOutboundsInterface; direct outbound UseIPv4. Платформенные options нужны для проверенного Linux IPv6/direct поведения, с regression checks Windows/Android.
- XrayProcessManager.StopCoreAsync: CloseMainWindow, затем Kill. Нужен Linux SIGTERM/grace timeout и process-group cleanup; общая process orchestration сохраняется.
- Xray run -test с TUN может создать интерфейс при инициализации. UI validation/loopback отделяются от TUN эксперимента в VM.
- AWG BuildRuntimeConfiguration намеренно запрещает внешний IPv6 Windows; Linux использует существующий BuildProxyRuntimeConfiguration и свою endpoint policy. Windows ограничение автоматически не снимается.
- Go bridge: отсутствуют bind_linux.go/owner_linux.go; udpBind.SetMark возвращает nil без установки метки; main слушает только os.Interrupt, нужен SIGTERM systemd. Добавить реальное binding/mark, надёжное owner tracking и Linux signals.
- Сохраняем userspace AWG/netstack/SOCKS; второй системный AWG adapter, собственный протокол и обязательный DKMS module для начала не требуются.

## Сеть, хранилище и desktop

Host владеет только своими TUN, route table/rules, resolved link settings и nft table; leases/journal и cleanup идемпотентны. Нет глобального reset/flush ruleset или перезаписи resolv.conf. DHCP изменения не откатываются старым общим snapshot.

DNS — systemd-resolved D-Bus per-link API; корпоративные domains/bootstrap DNS/LAN согласуются с kill switch. Firewall/Docker coexistence проверяется в VM. Подробная матрица — в связанном плане.

Linux profiles: AES-GCM файл 0600 и ключ Secret Service; Locked/unlock при закрытом keyring, без plaintext fallback. Host получает secrets через авторизованный IPC, временные configs защищены и исключены из diagnostics. Источник: [libsecret](https://gnome.pages.gitlab.gnome.org/libsecret/).

Linux config/data — XDG dirs; root runtime — `/run/ditunnel`, recovery journal — root-owned state dir. Автозапуск GUI после входа и автоподключение независимы; VPN до login отдельно. Общий MainWindow используется через X11/XWayland в первом варианте, native Wayland проверяется отдельно. Источник backend: [Avalonia platforms](https://docs.avaloniaui.net/docs/supported-platforms).

## Порядок реализации и критерии

Состояние базового подключения описано в [VPN этапе](../linux-vpn-stage.md): typed ip/resolvectl frontend реализует netlink/per-link D-Bus операции, полная служба использует root с CAP_NET_ADMIN, own table/rules и журнал. Namespace проверки не заменяют VM критерии ниже; crash fail-closed остаётся задачей nftables этапа.

1. **Разделить App/Windows зависимости**: явный store, descriptor/capabilities, release target, path comparison и совместимая миграция настроек. Существующие Windows/Android checks сохраняют поведение.
2. **Linux UI target**: отдельный entry, обычный пользователь, тот же App/XAML, startup/unlock/single instance. Capabilities включаются по мере готовности.
3. **Runtime proof без системного VPN**: Xray/AWG binaries, signals/owner, loopback probes/отмена; Linux сборки в WSL.
4. **Service и network transaction**: D-Bus/systemd/polkit, TUN/routes/resolved; Ubuntu VM с checkpoint.
5. **Kill switch/split/recovery**: nftables, process rules, IPv4/IPv6 и crash/coexistence tests; затем Ubuntu института.
6. **Packaging/integration**: `.deb`, update/checksum, autostart/notifications/tray; Android-only controls получают desktop аналоги позднее.

Core/App/Infrastructure tests на net10.0, но часть runtime tests пропускается вне Windows. Зелёный Linux запуск не доказывает Linux TUN/AWG/process lifecycle: нужны Linux runtime/platform tests. WFP tests остаются Windows. AsyncProbeLeaseGate сейчас связан в Infrastructure tests из Windows исходника; если он понадобится Linux, извлечь обычную synchronization logic в общую область.

Обязательные эксперименты до релиза: .NET D-Bus binding/serializer, Xray permissions `/proc`, sandbox apps, marks каждого outbound, tray/XWayland и transaction ordering nft/routes/DNS. Архитектура поддерживает перенос основных функций; работоспособность и полный паритет подтверждаются этими испытаниями.
