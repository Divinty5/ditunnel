# Linux: системная VPN-сессия

> Базовый этап VPN. Текущие РТ, KS, protocol v2, метки 51820/51821 и сохранение защиты после crash описаны в [следующем этапе](linux-protection-stage.md); результаты — в [отчёте VM](linux-vm-testing.md).


Дата: 7 октября 2026 года. Добавлены TUN, policy routes, transport bypass, per-link DNS, управление VPN через D-Bus и восстановление собственных объектов. Это базовое подключение всего трафика, без kill switch, split rules и блокировки рекламы. Выполнены [проверки в Ubuntu Desktop VM](linux-vm-testing.md); установочный пакет и полная протокольная матрица остаются следующими работами.

## Архитектура

Пользовательский `DiTunnel.Linux` передаёт `LinuxNetworkClient` как `IProfileVpnEngine` и `INetworkRecoveryEngine` общему Avalonia UI только при наличии соответствующей capability службы. UI не получает root. Не реализованные настройки разделения и рекламы отключены в Linux UI; сохранённые настройки других платформ не перезаписываются.

Служба принимает версию, UUID операции и один ограниченный профиль. Добавлены `Connect`, `Disconnect`, `GetStatus`, `RestoreNetwork`. Брокер D-Bus определяет sender; polkit авторизует Connect через `auth_admin_keep`, восстановление через `auth_admin`. Отключение своей сессии не требует повторного запроса пароля. Другой sender не может отменить живую сессию. Авторизованное восстановление может повторить очистку завершившейся с ошибкой сессии прежнего UI.

`LinuxVpnCoordinator` резервирует единственную сессию до ожидания polkit, обрабатывает отмену во время авторизации, исчезновение UI, ошибку запуска и завершение Xray/AWG. Успешный Disconnect подтверждается после очистки. При ошибке очистки сессия и журнал сохраняются для повторной попытки, новые подключения отклоняются.

`LinuxVpnSession` использует закреплённый Xray TUN; для AWG добавляет существующий authenticated loopback SOCKS/netstack мост. Linux builder удаляет неподдерживаемый `autoOutboundsInterface`. Удалённые сокеты Xray и зашифрованный UDP AWG получают SO_MARK 51820; AWG устанавливает метку до открытия UDP sockets. Привилегированные проверки серверов также получают эту метку, чтобы не измерять сервер через уже подключённый VPN.

## Сетевые объекты

На этом этапе typed adapter вызывает фиксированные `/usr/sbin/ip` и `/usr/bin/resolvectl` через ArgumentList, без shell и пользовательских команд. `ip` использует netlink, `resolvectl` — per-link D-Bus API systemd-resolved. Замена frontend на прямой managed netlink возможна за тем же интерфейсом `ILinuxNetworkCommands`.

- Для каждой сессии Xray создаёт непостоянный TUN `dtn<10 символов UUID>`. Host проверяет тип, сохраняет ifindex и задаёт alias `ditunnel:<UUID>`.
- Используются отдельная таблица 51820, route protocol 198 и priorities 100/101 для обеих семейств адресов. До изменений проверяется отсутствие занятой таблицы, имени интерфейса и несовместимых более ранних policy rules.
- Правило 100 направляет отмеченные сокеты в актуальную таблицу main. Правило 101 направляет неотмеченный трафик в собственную таблицу с default route через TUN. Существующие маршруты и DHCP snapshot не заменяются.
- Адреса TUN: `198.18.0.1/32`, `fd71:6469:7475::1/128`; MTU Xray 1400. Транспортный endpoint разрешается до изменения маршрутов. Смена uplink/MTU требует отдельной проверки и последующего этапа reconnect.
- После установки маршрутов задаются DNS, route-only domain `~.` и DefaultRoute только нашего интерфейса. Для Xray используются 1.1.1.1/1.0.0.1; AWG использует DNS общего converter. Глобальный resolv.conf и чужие link settings не изменяются. Более специфичные корпоративные DNS domains могут иметь приоритет; корпоративные сети требуют отдельной политики и VM проверки.

Rollback удаляет только точные собственные правила/маршруты, проверяет alias перед операциями с link/DNS, вызывает RevertLink своего интерфейса и останавливает runtime через pidfd/SIGTERM. Непостоянный TUN исчезает при закрытии ядра. Нет глобальных flush/reset. Формат iproute2 с `"not": null` явно поддержан.

## Журнал и systemd

`/run/ditunnel/vpn-owner.json` содержит только версию и UUID владельца, без профиля, сервера и ключей. Intent записывается атомарно с режимом 0600 до первого сетевого изменения. Конфигурации находятся в отдельном каталоге 0700/файлах 0600 и удаляются после остановки процессов.

Служба при старте восстанавливает журнал, затем предоставляет RPC. При ошибке recovery она сохраняет доступ к авторизованному RestoreNetwork. Неверный журнал не даёт права удалять произвольные объекты.

Шаблон systemd теперь запускает host как root с bounding set CAP_NET_ADMIN, доступом только к TUN device и AF_NETLINK. RuntimeDirectoryPreserve сохраняет журнал между рестартами, KillMode=control-group останавливает принадлежащие службе процессы. Без systemd cgroup один SIGKILL экспериментального host не гарантирует остановку Xray; session-development не используется как production service.

В production capability VPN доступна при root, наличии TUN/ip/resolvectl и resolv.conf с resolved stub 127.0.0.53. Обычный WSL DNS не переключается автоматически. `--session-development` остаётся режимом проверок без VPN. Отдельный `--namespace-vpn-development` разрешён только root в другом network namespace с отдельной system/session bus; это ограничение защищает первичную сеть при испытаниях.

## Проверки и пределы доказательств

Пройдены 332 .NET теста на Linux: Core 98, Infrastructure 88, App 106, Platform.Linux 40. Linux UI/host и Windows Desktop/NetworkHost собраны без ошибок и предупреждений. Native VPN smoke подтвердил TCP/UDP IPv4/IPv6, bypass, Disconnect, отказ DNS, crash Xray, исчезновение UI и recovery по журналу. Отдельно подтверждён запуск AWG с device-level fwmark и очисткой; удалённый AWG handshake и его TUN traffic здесь не проверялись. Runtime/probe smoke подтверждает прежние loopback, pidfd/SIGTERM, owner tracking и отмену D-Bus.

Автоматические тесты проверяют принадлежность сессии, ожидание cleanup, отмену авторизации, отказ polkit, конфликт маршрутов, точное удаление собственных правил и сохранение foreign alias. Общее состояние UI проверяется прежними тестами Windows/Android и Linux capabilities.

`Smoke-LinuxVpn.sh` создаёт два временных network namespaces, veth uplink, синтетический VLESS сервер и TCP/UDP endpoints. Настоящие Xray, TUN, IPv4/IPv6 policy routes и bypass проходят сквозную проверку. Настоящий resolvectl обращается к synthetic resolved manager по отдельной D-Bus шине: проверяются DNS, route-only root domain, DefaultRoute и RevertLink. Сеть Windows и основной namespace WSL не изменяются.

DNS wire test доказывает корректность вызовов per-link API, но не работу реального resolved cache, DNS routing или отсутствие DNS утечек на Ubuntu. Полная Ubuntu VM нужна для проверки GNOME polkit agent, реального resolved/NetworkManager, systemd restart/SIGKILL, DNS/IPv6 captures, конфликтов других VPN и смены сети. AWG и прочие варианты протоколов требуют собственной TUN матрицы; синтетический VLESS тест не доказывает их паритет.

Без nftables kill switch авария VPN возвращает обычную сеть после очистки. Защита от прямого fallback на этом этапе не заявляется. Корпоративный DNS, LAN bypass, split/adblock, process rules, sleep/reconnect и `.deb` относятся к следующим этапам.

Сборка и безопасные тесты:

```bash
bash scripts/Build-Linux.sh --test
bash scripts/Smoke-LinuxNetwork.sh
# После сборки DiTunnel.Linux.Smoke; root требуется только для одноразовых namespaces:
sudo --preserve-env=DOTNET_ROOT,DITUNNEL_BUILD_ROOT,CONFIGURATION bash scripts/Smoke-LinuxVpn.sh
```

Шаблоны службы/политик не установлены в основную WSL или Windows. Позднее в этот же день подготовлена отдельная Ubuntu Desktop VM: настоящий systemd/polkit/resolved и аварийная очистка проверены, границы приведены в [отчёте VM](linux-vm-testing.md). Существующая DiTunnel-KillSwitch-Test не изменялась. Локальная подготовка VM и сведения компьютера хранятся вне Git в `.tools`.

Источники схем: [ip rule](https://man7.org/linux/man-pages/man8/ip-rule.8.html), [Xray SocketConfig v26.3.27](https://github.com/XTLS/Xray-core/blob/v26.3.27/infra/conf/transport_internet.go), [systemd-resolved D-Bus API](https://www.freedesktop.org/software/systemd/man/latest/org.freedesktop.resolve1.html).
