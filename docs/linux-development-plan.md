# Di-Tunnel — план реализации для Ubuntu

Дата: 8 октября 2026 года. Реализованы Linux UI, runtime, системный VPN и [РТ/kill switch](linux-protection-stage.md). Настоящие systemd/resolved/polkit и сетевые аварии проверены в [Ubuntu Desktop VM](linux-vm-testing.md). Полный протокольный паритет, бесшовное переподключение под KS, интеграция рабочего стола и пакет ещё предстоят. Разделы архитектуры ниже сохраняют исходное обоснование; текущие ограничения приведены в документе защиты.

Конкретные границы проектов, повторное использование исходников, изменения общего UI и Linux компоненты уточнены в [ADR 0003](adr/0003-linux-platform-boundaries.md).

## Цель и границы первого релиза

Целевая система — Ubuntu 24.04 LTS Desktop, первоначально amd64. На компьютере в институте предположительно установлена 24.04; точную версию, архитектуру, рабочий стол и доступность административных прав нужно подтвердить. GNOME и Wayland пока являются предположением.

Нужен обычный пользовательский интерфейс Avalonia и отдельный привилегированный сетевой модуль. Общими остаются Core, импорт, подписки, конфигурации Xray, логика выбора серверов и большая часть UI. Основные VPN-возможности технически переносимы, но функциональный паритет можно заявить только после системной проверки. Android-виджеты, плитка и системный Always-on требуют других механизмов Linux.

Уточнено пользователем после VM испытаний: раздельное туннелирование обязательно для Linux-версии. В объём входят режимы «Обход выбранных» и «Только выбранные через VPN», правила доменов/IP и приложений. Версию с одним full tunnel нельзя считать завершённой поддержкой Linux. Критерий готовности — фактический правильный выход выбранного и контрольного TCP/UDP трафика, а не только доступность переключателей UI. Определение процессов, subprocess и sandbox-приложения проверяются отдельно; ограничения должны быть явно показаны.

Используем .NET 10 с SDK из `global.json` (сейчас 10.0.302) и Avalonia 12.1.2. Ubuntu 24.04 поддерживает .NET 10. Avalonia работает через X11; для первого этапа в сеансе Wayland проверяем XWayland. Нативный Wayland доступен отдельным opt-in backend и рассматривается после первого рабочего релиза. Ubuntu 24.x имеет у Avalonia уровень поддержки Tier 2, поэтому ручные проверки нашего UI обязательны. Источники: [Microsoft .NET Ubuntu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install), [Avalonia: платформы](https://docs.avaloniaui.net/docs/supported-platforms).

## Где разрабатывать и проверять

| Окружение | Назначение | Граница доказательств |
| --- | --- | --- |
| Сохранившийся WSL Ubuntu 24.04 | Linux-сборки, общие тесты, Go runtime, loopback-прокси; UI через WSLg | UI/host, Secret Service, runtime и отмена D-Bus проверены; базовый TUN TCP/UDP IPv4/IPv6 — в отдельных network namespaces. Реальный системный DNS/kill switch требуют VM |
| Отдельная Ubuntu 24.04 Desktop VM здесь | TUN, маршруты, DNS, kill switch, пакеты, аварийное восстановление, GNOME | Основное место изменений сети и fault injection; checkpoints и консоль обеспечивают восстановление |
| Ubuntu в институте | Подтверждение на реальном оборудовании: рабочий стол, сеть, сон, смена интерфейсов | Начинать после прохождения VM и проверки окружения; намеренные аварии сначала воспроизводить в VM |

WSL связан с сетью Windows через NAT либо mirrored networking; DNS также может обслуживаться механизмами Windows. Поэтому отсутствие утечки в WSL не доказывает отсутствие утечки на самостоятельной Ubuntu. Источник: [Microsoft: сеть WSL](https://learn.microsoft.com/en-us/windows/wsl/networking).

Предлагаемые ресурсы VM: Generation 2 Hyper-V, 2 vCPU, первоначально 4 ГиБ RAM, динамический диск 40–60 ГиБ. Это стартовая конфигурация, которую можно изменить по результатам сборки. Использовать Ubuntu Desktop amd64 из официального источника и проверить checksum. Ubuntu 24.04 поддерживается Hyper-V: [Microsoft: Ubuntu VM](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/supported-ubuntu-virtual-machines-on-hyper-v).

Перед созданием VM проверить работоспособность Hyper-V Manager/PowerShell и наличие подходящего виртуального коммутатора. Предпочесть существующий NAT-коммутатор; изменения внешнего коммутатора и адаптера Windows не входят в обычную подготовку Linux-клиента. Держать checkpoints чистой установки и состояния перед сетевыми испытаниями. IPv6 испытывать только после подтверждения работающего IPv6 у гостя: IPv4-only NAT не доказывает защиту IPv6.

Пути к локальным дискам, результат инвентаризации WSL/Hyper-V, VM и локальные настройки сборки хранятся в игнорируемой `.tools/linux-planning-local/`. Этот план переносим между компьютерами.

## Возможности и ожидаемый перенос

| Возможность | Ожидаемый результат Linux | Что требуется |
| --- | --- | --- |
| Импорт ссылок, подписок, VMess, AWG, ручные профили | Перенос общего кода | Файловые диалоги Linux, тесты импорта и защищённое сохранение |
| Hysteria 2, VLESS, VMess AEAD, Trojan, Shadowsocks; текущие транспорты | Предположительно тот же набор через Xray | Linux runtime нужной версии и отдельные TCP/UDP/HTTPS проверки каждого заявленного варианта |
| AmneziaWG, включая поддерживаемые параметры AWG 2.0 | Перенос существующего userspace backend | Linux UDP binding и lifecycle; тесты внешнего/внутреннего IPv4/IPv6, MTU и AllowedIPs |
| Системный VPN/TUN | Реализуемо | `/dev/net/tun`, Linux адреса/маршруты, обход туннеля для транспорта ядра |
| DNS через VPN и сохранение корпоративного DNS | Реализуемо с отдельной политикой | Интеграция systemd-resolved, bootstrap DNS, проверки пересечения доменных зон |
| Разделение по доменам/IP | Общая логика Xray | Проверка DNS/sniffing, обоих режимов и приоритета правил |
| Разделение по приложениям | Xray уже имеет Linux определение процессов | Список приложений Linux, корректные пути, доступ к `/proc`; отдельные проверки browser subprocess, TCP/UDP и sandbox |
| Блокировка рекламы и строгий режим | Общие правила и базы Xray | Закреплённые geodata и проверка порядка с split-правилами; прежние ограничения рекламы сохраняются |
| Быстрая/точная проверка серверов, страна и Lowest | Общая логика с Linux adapters | Loopback runtime, отмена и отсутствие осиротевших процессов; проверка не должна менять системные маршруты |
| Статистика, карта, языки, темы, подписки и выбор сервера | Большая часть общего UI | Проверка шрифтов, масштабирования, локализации, clipboard и file dialogs |
| Kill switch и разрешение LAN | Реализуемо через nftables | Новый backend, согласованная семантика split/direct, IPv4/IPv6 и проверка crash/recovery |
| Восстановление после потери сети, сна и перезапуска UI | Реализуемо | NetworkManager/netlink события, systemd lifecycle, журнал владения сетевыми объектами |
| Трей, уведомления, действие при закрытии | Зависит от рабочего стола | Проверить indicator в GNOME. При отсутствии трея нельзя прятать единственное доступное окно |
| Автозапуск и автоподключение после входа | Реализуемо | XDG autostart плюс разблокированное хранилище; два независимых переключателя |
| Работа VPN до входа пользователя, аналог Android Always-on/lockdown | Отдельный последующий этап | Загрузка службы/политики до трафика, доступ к секретам до входа; первый релиз этого не обещает |
| Проверка обновлений и установка | Новый Linux вариант | Платформа/архитектура/формат артефакта, `.deb`, checksum и отдельная авторизация установки |
| QR | Генерация переносима; ввод адаптируется | QR из изображения/буфера для первого релиза; камера с разрешениями и backend — последующий этап |
| Android-плитка и пять виджетов | Прямого универсального аналога нет | Предложить меню трея, launcher actions и ограниченные команды управления; GNOME extension необязательна |

Импорт произвольного Xray JSON сейчас не означает его исполнение даже на поддерживаемых платформах. Linux не должен расширять эту возможность через привилегированный модуль без отдельного решения.

## Подтверждённые ограничения исходников

1. `src/DiTunnel.Desktop/DiTunnel.Desktop.csproj` нацелен на Windows, ссылается на Windows platform/host, копирует `.exe` и `wintun.dll`. Его сборка с `linux-x64` сама по себе не создаст Linux-клиент.
2. `src/DiTunnel.Desktop/Program.cs` создаёт Windows adapters, запускает UAC `runas`, Windows updater и именованный `EventWaitHandle`. Для Linux нужен отдельный composition root и механизм единственного экземпляра с активацией окна через Unix socket или D-Bus.
3. `src/DiTunnel.App/ProfileStorage.cs` использует Windows DPAPI и явно отказывает вне Windows. Linux должен передавать реализацию `IProfileStore` в существующий конструктор `MainViewModel`.
4. В `UserSettings.cs`, `Views/Dialogs.cs` и `ViewModels/MainViewModel.cs` есть Windows подписи, `StartWithWindows`, выбор `.exe`, проверки только Android/Windows и Windows описание runtime. Признак «не Android» сейчас не доказывает наличие kill switch на Linux. Нужны явные capabilities и фактический статус backend.
5. `UserSettings.NetworkSettingsFingerprint()` приводит все process rules к нижнему регистру; UI местами удаляет дубликаты через `OrdinalIgnoreCase`. Linux пути чувствительны к регистру. Доменные имена и application IDs/пути должны нормализоваться раздельно, с сохранением прежнего поведения Windows/Android.
6. `src/DiTunnel.Core/Connection/KillSwitchRulePlan.cs` содержит Windows `InterfaceLuid`, а `KillSwitchConfiguration.cs` — Windows ориентированные детали приложения. Не переносить WFP-модель как готовую nftables модель. Выделить общие намерения политики и оставить платформенные идентификаторы в adapters.
7. `src/DiTunnel.App/ReleaseChecker.cs` при `android=false` выбирает Windows Setup `.exe`; App metadata сейчас знает только WindowsVersion/AndroidVersion. Нужны LinuxVersion и выбор platform/architecture/package вместо boolean.
8. `runtime/amneziawg/main.go` включён для `!android`, но `watchOwner` реализован только в `owner_windows.go`. Общий `bind.go` вызывает `bindInterface`, имеющий Windows/Android реализации. Для Linux отсутствуют обе реализации; одной смены GOOS недостаточно.
9. `eng/xray-version.json` и `eng/amneziawg-runtime.json` описывают Windows download assets/toolchain. Добавить отдельные Linux артефакты с checksum, сохранив существующие pins для Windows и Android.

### Что уже есть в закреплённом Xray

Для начального Linux эксперимента рассматриваем Xray **v26.3.27**, закреплённый сейчас для desktop. В исходниках именно этой версии Linux TUN открывает `/dev/net/tun`, создаёт интерфейс, задаёт MTU и поднимает link. Настройку адресов, маршрутов и DNS должен выполнять наш host. Источник: [tun_linux.go v26.3.27](https://github.com/XTLS/Xray-core/blob/v26.3.27/proxy/tun/tun_linux.go).

В `infra/conf/tun.go` этой версии есть только `name`, `MTU`, `userLevel`. Новые поля автоматической маршрутизации из актуальной документации нельзя считать доступными в закреплённом runtime. Linux config builder должен опираться на проверенную схему. Обновление Xray при необходимости оформляется отдельным изменением с повторной проверкой Windows. Источник: [TunConfig v26.3.27](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/tun.go).

Определение приложения уже реализовано: lookup socket inode в `/proc/net/tcp*`/`udp*`, поиск PID и чтение `/proc/<pid>/exe`. Это подтверждает техническую основу process rules, но не успешную работу с любым приложением. Проверить права, `hidepid`, короткоживущие UDP-сокеты, несколько процессов браузера, Flatpak/Snap и network namespaces. Источник: [find_process_linux.go v26.3.27](https://github.com/XTLS/Xray-core/blob/v26.3.27/common/net/find_process_linux.go).

## Предлагаемая архитектура

Добавлять постепенно:

```text
DiTunnel.Linux                  net10.0, Avalonia Desktop, пользовательский UI
    ├── DiTunnel.App / DiTunnel.Core
    └── DiTunnel.Platform.Linux  IPC client, profiles, desktop integration
                  │
          типизированный IPC + polkit
                  │
DiTunnel.NetworkHost.Linux       systemd system service, сетевые права
    ├── Xray Linux TUN / userspace AmneziaWG
    ├── netlink: адреса, маршруты и policy rules
    ├── systemd-resolved: DNS нашего интерфейса
    └── nftables: собственная таблица защиты
```

Отдельная Linux точка входа позволяет начать без изменения Windows target framework и packaging. После получения рабочего клиента извлекать действительно общие desktop части, если это уменьшает дублирование.

Переиспользовать `IVpnEngine`, `IProfileVpnEngine`, `IServerProbe`, `IServerCountryResolver`, `IInstalledApplicationProvider`, `INetworkRecoveryEngine`, `INetworkProtectionController` и `IProfileStore`. Общий App должен знать возможности adapters, а не угадывать их по ОС.

### Привилегии, IPC и секреты

- UI работает без root. Установленный root-owned host выполняет ограниченные операции подключения, отключения, проверки, статуса и восстановления; авторизация через polkit. Предлагаемый транспорт — system D-Bus; Unix socket допустим при проверке peer credentials и эквивалентной авторизации. Источник: [polkit](https://polkit.pages.freedesktop.org/polkit/polkit.8.html).
- IPC имеет версию, ограничение размера, проверку схемы, отмену и контроль владельца сессии. Существующие семантические команды можно переиспользовать; Windows named pipe/UAC transport заменяется.
- Host не исполняет произвольные shell-команды, не принимает путь к произвольному root executable или неограниченный Xray JSON. Runtime/configuration templates принадлежат установленному пакету, writable пользовательские файлы не исполняются с root.
- Минимизировать права дочерних runtime. Сначала проверить, какие права реально нужны закреплённому Xray для TUN и `/proc`; не обещать передачу готового TUN FD/сброс прав без проверки версии. `CAP_NET_ADMIN` сам по себе не гарантирует чтение чужих `/proc/<pid>/fd`.
- Профили хранить в пользовательском каталоге XDG в зашифрованном файле с правами 0600, например AES-GCM; ключ — в Secret Service через libsecret либо совместимый D-Bus client. Закрытый/недоступный keyring требует разблокировки или понятного отказа, без plaintext fallback. Источник: [libsecret](https://gnome.pages.gitlab.gnome.org/libsecret/).
- Секреты передавать через авторизованный IPC. Если runtime нужен временный config, создавать его в защищённом runtime directory с 0600 и удалять после использования; не передавать секреты аргументами командной строки и не записывать в логи.
- Зашифрованные Windows/Android файлы не переносимы автоматически. Первоначальный перенос профилей — повторный импорт пользователем; отдельный безопасный export/import можно спроектировать позднее.

### Владение сетью и восстановление

1. До изменения сети проверить конфликт имён/маршрутных таблиц/приоритетов и определить физический egress. Сохранить журнал только собственных объектов и минимальные исходные данные для восстановления; не держать в публичной документации реальные адреса.
2. Зарезервировать собственные TUN, routing table, policy rules и метки транспорта. Исключить захват исходящих сокетов Xray/AWG собственным TUN; проверить endpoint hostname, смену IP, IPv4/IPv6, bootstrap DNS и смену интерфейса.
3. На старте подключения с kill switch устанавливать ограниченную защиту до перенаправления пользовательского трафика. После готовности runtime согласованно включать маршруты и DNS; статус Connected/Protected выдавать после проверки фактического состояния.
4. Linux host управляет адресами и маршрутами через netlink; shared Xray builder формирует платформенный TUN config и socket options. Не дублировать управление одними объектами между Xray, host и NetworkManager.
5. Использовать per-link DNS systemd-resolved: DNS серверы, routing domains и default-route нашего link. Для обычного VPN `~.` направляет общий DNS в туннель; более специфичные корпоративные зоны могут оставаться у прежнего link. Одинаково специфичные зоны могут вызвать параллельные запросы — конфликт нужно выявлять. Не перезаписывать `/etc/resolv.conf` или глобальные настройки NetworkManager. Источник: [systemd-resolved и VPN](https://github.com/systemd/systemd/blob/main/docs/RESOLVED-VPNS.md).
6. Политику корпоративного DNS согласовать с режимом LAN/kill switch: исключение резолвера должно быть узким и явным. DNS после выбора приложения нельзя автоматически считать отдельным per-app каналом: системный resolver общий, браузеры могут использовать DoH.
7. При отключении удалять только наши маршруты/rules/filter objects и отменять DNS нашего link. При смене DHCP/сети не восстанавливать устаревший общий snapshot поверх текущих настроек системы.
8. Служба ведёт ограниченный журнал транзакции и выполняет идемпотентное восстановление после своего перезапуска. Закрытие UI и добровольный Disconnect имеют явно выбранную семантику; для первого релиза разумно сохранить desktop поведение штатного завершения VPN при выходе.

### Kill switch

Собственная таблица семейства `inet` охватывает IPv4/IPv6. Правила устанавливаются согласованными batches, имеют owner/session identity и не затрагивают чужие таблицы. Исключения транспорта ограничены доверенным runtime, endpoint/port/protocol либо проверенными метками; loopback, необходимые DHCP/ICMPv6 и выбранный LAN обрабатываются отдельно.

Заранее определить совместное поведение с `ProxyAll`, `BypassSelected`, `ProxySelected`: намеренно прямой трафик при работающем VPN должен соответствовать настройкам. Direct outbound и транспорт VPN не должны получать одно безусловное широкое исключение. Потеря runtime не должна превращать всё устройство в разрешённый direct egress.

Не использовать `flush ruleset`, отключение UFW или удаление Docker rules. `accept` в нашей цепочке не отменяет `drop` другой цепочки; совместимость проверяется при работающем firewall и его reload. Источник: [nftables: chains](https://wiki.nftables.org/wiki-nftables/index.php/Configuring_chains).

При crash runtime сохраняется блокировка непредусмотренного direct трафика; авторизованное штатное отключение удаляет нашу защиту и возвращает сеть. Перезапуск systemd service и ранний boot требуют отдельной проверки порядка запуска. Защиту до входа/после reboot нельзя обещать до реализации persistence и boot ordering.

Реализован отдельный жизненный цикл firewall: блокировка сохраняется после потери UI, runtime и host, а аварийная очистка маршрутов не удаляет её автоматически. Политика до входа пользователя и после reboot остаётся отдельным этапом.

Транспорт имеет метку 51820, прямой outbound — 51821. В nftables метка проверяется вместе с UID и cgroup службы; транспорт дополнительно ограничен endpoint/port/protocol. При аварии пользовательский Интернет блокируется до восстановления либо явного отключения защиты. LAN обрабатывается своим переключателем; plain DNS 53 принудительно направляется через VPN. Процессные правила требуют исправленного Linux Xray runtime с проверенной metadata. Подробности — в [архитектуре защиты](linux-protection-stage.md).

### AmneziaWG и список приложений

- Реализован `bind_linux.go`: Linux socket binding/mark и физический egress, отдельно IPv4/IPv6. Не заменять реальное исключение из TUN пустой заглушкой.
- Реализован `owner_linux.go`: надёжный контроль владельца через control channel, pidfd либо согласованный systemd lifecycle; учитывать повторное использование PID, SIGTERM и завершение всей группы процессов. Linux обрабатывает SIGTERM и устанавливает реальную socket mark; запуск/завершение проверены в namespace. Реальный AWG handshake ещё требуется.
- Сохранить схему userspace AWG → локальный SOCKS bridge → Xray TUN. На первом этапе отдельный kernel module/DKMS не требуется; собственную реализацию AWG не создаём.
- Получать список desktop applications из XDG `.desktop` entries плюс явно выбранные исполняемые файлы. Desktop ID, отображаемое имя и фактический путь процесса — разные поля. `Exec` может запускать wrapper/launcher: не считать его автоматически именем всех сетевых процессов.
- Проверить уже работающие приложения и subprocess; process rules сопоставлять с реальным `/proc/.../exe` без изменения регистра. Если sandbox/permissions мешают, явно показывать ограничение. cgroup-managed launcher рассматривать как последующий вариант, только если Xray process lookup недостаточен.

## Этапы с критериями готовности

| Этап | Работа | Критерий перехода |
| --- | --- | --- |
| 0. Окружения и ADR | Уточнить Ubuntu института; подготовить WSL toolchain и Desktop VM; подтвердить pins Linux Xray/Go; определить права, IPC и владение сетью | VM имеет checkpoint/консоль, WSL собирает выбранные общие проекты; архитектурные решения записаны |
| 1. Linux UI без системного VPN | Linux entry point, capabilities, хранилище, XDG paths, single instance, версии, dialogs | UI обычного пользователя работает в GNOME, профили переживают перезапуск, Windows/Android checks проходят |
| 2. Runtime и loopback | Linux Xray asset, Linux AWG binding/lifecycle, probes/Lowest, TCP/UDP proxy | Протоколы и AWG проходят loopback тесты; отмена не оставляет processes/config; системные маршруты не меняются |
| 3. TUN и DNS в VM | Linux host/systemd/polkit, адреса, маршруты, runtime bypass, resolved DNS, Disconnect/recovery | Подтверждены VPN egress TCP/UDP IPv4/IPv6 и восстановление сети; ошибки каждого шага откатываются |
| 4. Защита и split tunneling | nftables kill switch, domain/IP/process rules, LAN, adblock, crash/reconnect | Полная матрица в VM, включая packet capture; UI корректно сообщает защиту и её отказ |
| 5. Desktop интеграция и пакет | Трей, уведомления, autostart, `.deb`, обновление/удаление | Чистая VM устанавливает и обновляет пакет, удаление освобождает собственные сетевые объекты; доступ к UI сохраняется без трея |
| 6. Институт и релиз | Подтверждение на Ubuntu физического компьютера, регрессии Windows/Android, Linux validation report | Обезличенные результаты обоих Linux окружений; опубликованные ограничения соответствуют проверкам |

Сначала получить безопасный UI и loopback, затем первый TUN, затем защиту и split. Сроки оцениваются после этапа 2: основные неопределённости — permissions process lookup, DNS корпоративной сети и совместимость firewall.

## Матрица испытаний

| Проверка | Где | Ожидаемое доказательство |
| --- | --- | --- |
| Сборка/импорт/настройки/маршрутный план | WSL и Linux CI | Тесты Core/App/Infrastructure; Linux case-sensitive paths, platform asset selection, keyring ошибки |
| TCP, UDP, DNS, HTTPS по каждому заявленному протоколу | Loopback, затем VM | Проверяемый сервер/endpoint; HTTP не считается доказательством UDP |
| Full tunnel IPv4 и IPv6; IPv4-only профиль | VM, затем институт | Egress через VPN; неподдерживаемое семейство блокируется, а не выходит напрямую |
| DNS, TCP/UDP 53, DoH, корпоративные зоны | VM, затем институт | Capture на физическом guest интерфейсе и тестовые запросы; разрешённые исключения описаны отдельно |
| Доменные/IP правила, оба split режима, adblock | VM, затем институт | Правильный egress у выбранного и контрольного трафика; порядок правил проверен |
| Process rules: browser, child processes, CLI, UDP, одинаковый basename в разных путях | VM, затем институт | Результат определяется реальным executable path; отдельно Snap/Flatpak и ограничения `/proc` |
| Kill switch при kill Xray/AWG/host/UI, недоступном сервере и каждой промежуточной ошибке | Сначала только VM | Нет непредусмотренного direct IPv4/IPv6/DNS; UI не показывает ложный Active |
| Смена адреса/интерфейса, Ethernet/Wi-Fi, sleep/wake, reconnect | VM где воспроизводимо; затем институт | Новая сеть работает, stale routes/DNS и старые processes отсутствуют |
| UFW, Docker, корпоративный VPN и их reload | Изолированная VM | Чужие объекты сохраняются; конфликт явно выявляется; политика не исчезает молча |
| LAN разрешён/запрещён, mDNS, SSH к guest | VM | Выбранная политика соблюдается; восстановление возможно через Hyper-V console |
| GNOME Wayland/XWayland, X11, масштаб 100/150/200%, отсутствие indicator | VM, затем институт | UI доступен, dialogs/clipboard/notifications работают; закрытие не теряет окно |
| Install/upgrade/downgrade failure/uninstall/reboot | Чистая VM | Профили сохраняются, service/polkit/runtime согласованы, собственные сетевые изменения очищаются |

На компьютере института перед подключением подтвердить наличие локальной консоли, права установки и согласованность с действующим VPN/firewall. Первые проверки — чтение окружения и loopback. Намеренные аварии на рабочем компьютере не нужны для первичного подтверждения.

До вмешательства локально сохранить сетевой baseline; после Disconnect сравнить собственные объекты и актуальное состояние сети. Captures, реальные профили, subscriptions, server addresses и неочищенные журналы остаются вне Git. В отчёте хранить метод, результат, версии и обезличенные доказательства.

## Сборка, установка и обновления

- Общие build scripts и runtime manifests содержат платформу/RID, версию, URL и SHA-256, без абсолютных путей конкретного компьютера. Linux SDK/Go/download caches находятся в `.tools` либо пользовательском окружении. Windows SHA-256 не используется для Linux архива.
- Первый пакет — `.deb` amd64, self-contained .NET приложение, проверенные Xray/AWG и лицензии. Installer устанавливает root-owned host/runtime, systemd unit, polkit policy и desktop entry. Linux ARM64 — отдельный последующий build/test target.
- Одна Linux build команда подготавливает pinned dependencies, публикует UI/host, создаёт `.deb` и checksum. Linux CI собирает выбранные portable/Linux проекты, не Windows/Android solution целиком.
- Проверка обновлений выбирает только Linux пакет подходящей архитектуры с checksum. LinuxVersion независима от WindowsVersion/AndroidVersion. Недоступный Linux asset не должен предлагать `.exe` или APK.
- До обновления штатно отключать VPN; запуск пакетного менеджера через ограниченный механизм авторизации. Настройка автообновления не даёт UI постоянные root права. Проверить отказ установки, rollback и сохранение keyring/profile data.
- XDG autostart относится ко входу в desktop session; автоматическое подключение требует доступа к профилю после unlock. Это отдельные настройки. Источник: [XDG Autostart](https://specifications.freedesktop.org/autostart/latest/).
- Проверка опубликованного SHA-256 подтверждает целостность относительно полученного manifest, а не заменяет доверие к каналу релиза. Подписанный репозиторий пакетов рассматривается отдельно.

## Что уже проверено и что предстоит

Проверены исходники и версии зависимостей; существующий WSL Ubuntu запущен командами чтения, внутри подтверждены Ubuntu 24.04.3 x86_64, systemd и наличие TUN device. Подробности локального окружения исключены из Git.

Linux UI, Secret Service, runtime, loopback, TUN, настоящий resolved/polkit и восстановление systemd проверены в VM. Реализованы оба режима РТ по доменам/IP и фактическим executable, nftables KS, LAN и сохранение защиты после аварии. Результаты и точные границы — в [отчёте VM](linux-vm-testing.md).

Для Linux 0.6.0 реализован self-contained `.deb` amd64: systemd/polkit, ярлык, XDG autostart, проверка Linux обновлений и установка через polkit/apt. В VM проверены установка, upgrade при активном VPN, отказ удаления при неудачном восстановлении, remove, purge и переустановка с сохранением пользовательских данных. Инструкции — в [руководстве установки](linux-installation.md), результаты — в [отчёте VM](linux-vm-testing.md). Windows/Android остаются 0.5.19.

Ближайшие действия: проверка на Ubuntu 24.04 в институте; бесшовное переподключение под сохранённым KS; протокольная матрица с реальным AWG handshake; suspend/resume, смена uplink и firewall reload. Автозапуск пока открывает видимое окно без автоматического VPN; GNOME tray не заявлен. Загрузка опубликованного обновления требует отдельной проверки после публикации релиза. Защита до входа пользователя и после reboot — отдельный этап.
