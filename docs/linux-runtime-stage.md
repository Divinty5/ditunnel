# Linux: runtime, проверки серверов и D-Bus

Дата: 7 октября 2026 года. Этот документ фиксирует этап runtime/loopback. В следующем [этапе системного VPN](linux-vpn-stage.md) уже добавлены TUN, маршруты, per-link DNS и recovery; нынешние шаблоны службы расширены для этих возможностей. Kill switch, установочный пакет и Ubuntu Desktop VM ещё предстоят.

## Что работает

- Закреплён Linux amd64 Xray 26.3.27 с отдельным SHA-256 Linux архива. `Prepare-LinuxRuntime.py` проверяет архивы Xray/Go, версию и checksum модуля AWG, собирает мост, сохраняет лицензии и хеши runtime/geodata.
- AWG использует существующий netstack и SOCKS backend. Добавлены Linux binding по интерфейсу, SO_MARK с очисткой прежней метки, pidfd для наблюдения за владельцем и SIGTERM. Android protection и Windows interface binding сохранены.
- Xray получает Linux lifecycle через опциональный `ProcessLifetimeFactory`: pidfd закрепляется за запущенным процессом, остановка посылает SIGTERM, таймаут или отказ graceful shutdown приводит к принудительной очистке. Windows сохраняет прежний механизм по умолчанию.
- Проверки ожидают ответ SOCKS negotiation вместо предположения, что фиксированной задержки запуска достаточно. Это устранило воспроизведённый отказ подключения к ещё не готовому listener под нагрузкой.
- `LinuxRuntimeServerProbe` повторно использует converters и `XrayServerProbe`. Fast проверяет HTTP через Xray; для AWG Fast ожидает удалённый TLS handshake. Https проверяет HTTPS. DNS/bootstrap и процессы входят в бюджет проверки; конфигурации создаются в отдельном каталоге `0700`, файлы — `0600`, затем удаляются.
- `DiTunnel.NetworkHost.Linux` предоставляет `GetCapabilities`, `Probe`, `CancelProbe` через D-Bus. UI получает `IServerBatchProbe`, сохраняет общий механизм измерений/сортировки и выполняет пакет последовательно. VPN engine пока не передаётся: кнопка подключения остаётся недоступной.

IPC передаёт содержимое профиля без отдельных полей названия, URL подписки и статистики. Протокол версии 1 принимает один поддерживаемый профиль размером до 2 МиБ UTF-8; произвольный Xray JSON, несколько профилей и неизвестная версия отклоняются. Runtime paths и shell-команды в IPC отсутствуют. Ответы и service journal не содержат профилей, credentials или сырой stderr runtime.

## Права и жизненный цикл

Служба этого этапа выполняет только проверки и запускается под отдельным непривилегированным пользователем `ditunnel`. Шаблон systemd не предоставляет capabilities и закрывает устройства. TUN требует отдельного расширения службы и системных испытаний в VM; текущий шаблон намеренно не разрешает сетевые изменения.

Системный D-Bus разрешает владеть именем службы только её пользователю. Host получает Unix UID от брокера и передаёт polkit субъект `system-bus-name` с реальным уникальным sender. Action `org.divinty5.ditunnel.probe` разрешает проверки активной локальной сессии; для VPN control потребуется отдельный action. Постоянных разрешений root для UI и blanket polkit rules нет.

Одновременно допускаются четыре проверки, по одной на D-Bus connection. Локальный клиент последовательно отправляет пакет, поэтому общие параллельные UI-проверки не создают конфликт своих leases. Отмена относится только к sender + operation ID и отвечает после остановки backend. Исчезновение sender и остановка host отменяют его leases. Systemd `KillMode=control-group` служит дополнительной границей для процессов установленной службы.

В developer режиме `--session-development <runtime-directory> <state-directory>` host работает исключительно на пользовательской шине и принимает проверки только от того же UID, проверенного брокером. Этот режим не заменяет production polkit и не выполняет системных сетевых действий. UI выбирает его только при явном `--network-session-development`.

## Воспроизводимая проверка на Ubuntu amd64

Нужны .NET SDK из `global.json`, Python 3.12+, `dbus-run-session` и сеть для первой загрузки зависимостей:

```bash
python3 scripts/Prepare-LinuxRuntime.py --test
bash scripts/Build-Linux.sh --test
bash scripts/Smoke-LinuxNetwork.sh
```

`Prepare-LinuxRuntime.py` сохраняет runtime в игнорируемой `.tools/linux-runtime/linux-x64`. На checkout через `/mnt/...` распаковка SDK и Go cache значительно медленнее; можно задать `DITUNNEL_LINUX_CACHE="$HOME/.cache/ditunnel-build"` на файловой системе Linux. Готовые runtime остаются в `.tools`. Скрипт не устанавливает службы, пользователей или политики.

Build и smoke принимают `DOTNET_ROOT`, `CONFIGURATION`, `DITUNNEL_BUILD_ROOT`; smoke использует новую изолированную D-Bus-сессию, синтетические профили и временный каталог. Для ручной работы в developer сессии сначала запустить host с двумя абсолютными путями, затем UI с `--network-session-development` в той же сессии. После установки системной службы достаточно обычного запуска UI; отсутствие службы не мешает хранению профилей. Обнаружение службы сейчас происходит при запуске приложения, поэтому после её установки нужен перезапуск UI.

В `packaging/linux` подготовлены unit, D-Bus policy и polkit action. На этом этапе это шаблоны для будущего `.deb`, без installer; последующая упаковка описана в [руководстве установки](linux-installation.md). Служба работает от root с ограниченными capabilities и не требует отдельного sysusers account. Установленная служба ожидает host в `/usr/lib/ditunnel/host`, runtime в `/usr/lib/ditunnel/runtime`, временное состояние в `/run/ditunnel`. Эти файлы должен устанавливать пакет с владельцем root; текущий этап их в систему не копирует.

## Проверено и ограничения

- Ubuntu WSL: Core 98, Infrastructure.Xray 88, App 106, Linux platform 29 — всего 321 тест. Linux UI/host собраны без compiler warnings.
- Go: Linux AWG tests/vet и сборка пройдены; тесты включают зашифрованный TCP/UDP, DNS/TLS, композицию с настоящим Linux Xray, отсутствие direct fallback и Linux lifecycle. Из 15 основных тестов 14 выполнены обычным пользователем; тест установки/очистки SO_MARK сначала пропущен из-за отсутствующих capabilities, затем отдельно пройден с правами root в новом сетевом namespace WSL, без изменения основной сети. Windows Go tests также пройдены; Android ARM64 native bridge пересобран.
- Реальный smoke: HTTP через loopback SOCKS Xray, штатный SIGTERM Xray/AWG, выход AWG после смерти владельца, D-Bus capabilities, отклонение JSON/слишком большого запроса, отмена с очисткой, потеря клиента и права `0600`. Wire format polkit, broker credentials и allow/deny проверены с подставной Authority на изолированной шине.
- Windows: Desktop/NetworkHost build и релевантные общие тесты пройдены. Android Debug ARM64 build пройден; остались 33 прежних binding/nullable/Camera предупреждения.
- Синтаксис shell/Python/XML проверен. Systemd unit проверен `systemd-analyze verify` на временной копии с заменой executable/user на существующие: установка службы и её policy в реальном GNOME ещё не проверены.

Loopback proof подтверждает транспорт AWG и композицию с Xray, но не подтверждает все варианты VLESS/VMess/Trojan/Shadowsocks/Hysteria 2 на Linux, внешний IPv6, системный VPN, split, kill switch или отсутствие сетевых утечек. Для этих проверок остаётся матрица из [плана](linux-development-plan.md). Автоматическое переключение Lowest во время VPN ожидает полноценный engine; измерения и сортировка используют общий UI.

Следующий этап — Ubuntu Desktop VM с checkpoint, TUN и обход транспорта, owned routes, per-link systemd-resolved DNS, Disconnect/rollback и обработка аварий. Ubuntu VM здесь ещё не подготовлена; институтский компьютер на этом этапе не использовался.

Источники pins/API: [Xray release 26.3.27](https://github.com/XTLS/Xray-core/releases/tag/v26.3.27), [Go downloads](https://go.dev/dl/), [Tmds.DBus protocol API](https://github.com/tmds/Tmds.DBus/blob/491bde2c16d65a7904397933cffa24e15e45eb2a/docs/protocol.md), [polkit Authority](https://www.freedesktop.org/software/polkit/docs/latest/eggdbus-interface-org.freedesktop.PolicyKit1.Authority.html), [systemd.exec](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html).
