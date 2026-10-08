# Linux: первый этап реализации

Дата: 7 октября 2026 года. Цель: Ubuntu 24.04 Desktop amd64. Это оболочка для разработки, без подключения VPN и без опубликованного `.deb`.

Архитектура: [ADR 0003](adr/0003-linux-platform-boundaries.md). Следующие этапы и матрица системных испытаний: [план Linux](linux-development-plan.md).

## Реализовано

- `DiTunnel.Linux`: отдельная точка входа с существующими App, XAML и ViewModels; обычные пользовательские права.
- `DiTunnel.Platform.Linux`: каталоги XDG, блокировка второго экземпляра, профильный файл AES-256-GCM и интеграция Secret Service через `/usr/bin/secret-tool`.
- `AppPlatform`: платформенные подписи, возможности kill switch/трея, правила сравнения путей и цель обновления. Фактический статус сетевой защиты по-прежнему определяет engine.
- DPAPI-хранилище перенесено из App в `WindowsProfileStore`; Windows entry явно передаёт его в MainViewModel. Формат и путь `%LOCALAPPDATA%/DiTunnel/profiles.dat` сохранены.
- Общий UI не предлагает подключение без engine и блокирует импорт/изменение профилей при недоступном хранилище. Ошибка чтения не превращается в пустой файл.
- Linux не выбирает Windows `.exe` или APK; зарезервирован asset `Di-Tunnel-<версия>-linux-amd64.deb` вместе с SHA-256. Установщик и автообновление Linux ещё не реализованы.
- Учитывается регистр Linux executable paths в fingerprint сетевых настроек; домены остаются без учёта регистра.
- Новые сообщения переведены на английский, испанский и упрощённый китайский.

## Сборка и запуск на Ubuntu

Нужен .NET SDK, совместимый с `global.json` (сейчас 10.0.302), и графическая сессия X11/XWayland. Для первого варианта используем стандартный backend Avalonia. Библиотеки и provider Secret Service устанавливаются в тестовую Ubuntu:

```bash
sudo apt-get update
sudo apt-get install --no-install-recommends libx11-6 libice6 libsm6 libfontconfig1 libsecret-tools gnome-keyring
bash scripts/Build-Linux.sh --test
dotnet artifacts/linux/build/DiTunnel.Linux/bin/Release/net10.0/Di-Tunnel.Linux.dll
```

В обычном GNOME связка ключей доступна через пользовательский D-Bus. На headless/WSL установка пакета не гарантирует разблокированную связку: для тестов используется отдельная `dbus-run-session`, а не смена общесистемных сетевых настроек. При ошибке/таймауте Secret Service окно открывается с пояснением, импорт недоступен. Разблокировка и повторное открытие хранилища пока выполняются перезапуском приложения.

Скрипт принимает `--test`; `CONFIGURATION`, `DOTNET_ROOT` и `DITUNNEL_BUILD_ROOT` задаются окружением. По умолчанию сборка находится в игнорируемом `artifacts/linux/build`. Локальные SDK, кэши и тестовые логи остаются в `.tools`, пути конкретного компьютера не входят в проекты.

## Данные и жизненный цикл

| Назначение | Каталог по умолчанию |
| --- | --- |
| Настройки | `$XDG_CONFIG_HOME/ditunnel`, иначе `~/.config/ditunnel` |
| Зашифрованные профили и instance lock | `$XDG_DATA_HOME/ditunnel`, иначе `~/.local/share/ditunnel`; профили в `profiles.dat` |
| Состояние и журналы | `$XDG_STATE_HOME/ditunnel`, иначе `~/.local/state/ditunnel` |

Относительные значения XDG игнорируются. AppPaths настраиваются до первого чтения UserSettings.Current; Windows/Android используют прежние каталоги. Источник правил: [XDG Base Directory Specification](https://specifications.freedesktop.org/basedir/latest/).

Ключ AES-256 хранится только в Secret Service; атрибуты связывают его с абсолютным путём профильного файла. Ключ не передаётся через argv и не сохраняется в локальном key file. Новый ключ создаётся только для отсутствующего файла, затем повторно считывается для проверки. Потерянный ключ существующего файла не заменяется автоматически. Перенос профильного файла в другой XDG-каталог требует отдельной миграции ключа; экспорт/backup workflow ещё предстоит реализовать.

Формат файла версионирован, заголовок входит в authenticated data; для каждой записи используется новый nonce. Замена выполняется через отдельный файл с правами `0600` и атомарный rename. После неуспешного Load Save запрещён. Исходник CLI интеграции: [GNOME secret-tool](https://github.com/GNOME/libsecret/blob/main/tool/secret-tool.c).

Второй экземпляр завершается с сообщением; активация окна через IPC ещё не реализована. Скрытие в трей выключено до проверки фактической доступности tray host. Пользователь может закрыть окно через выход; старая настройка `hide` переводится в запрос действия при закрытии.

## Следующий этап

Runtime Xray/AWG, проверки серверов через D-Bus и шаблоны службы реализованы в [этапе runtime/loopback](linux-runtime-stage.md). TUN/routes/resolved, nftables и recovery остаются следующими этапами. Также остаются desktop application discovery, autostart, updater и упаковка. StartWithWindows пока сохранён для совместимости настроек; миграция в StartAtLogin выполняется вместе с desktop integration.

Сетевые эксперименты выполняются в Ubuntu VM с checkpoint. Текущая оболочка не создаёт TUN и не меняет маршруты, DNS или firewall.

## Проверено 7 октября 2026 года

- Ubuntu 24.04.3 WSL: сборка Linux entry завершена без предупреждений и ошибок; 98 Core, 106 App и 14 Linux platform tests пройдены (всего 218).
- В отдельной D-Bus-сессии с временными XDG-каталогами: реальный GNOME Secret Service lookup/store/reopen, AES-GCM round trip и права `0600` подтверждены на синтетическом профиле.
- Реальный Avalonia X11 window через WSLg открыт; второй экземпляр завершается и сохраняет первое окно.
- Windows: Desktop/NetworkHost собраны; 98 Core, 106 App и 14 Linux platform tests пройдены. Restore Windows сохранил предупреждение NU1900 о недоступном источнике vulnerability metadata; новых compiler warnings общего UI нет.
- Android: Debug ARM64 build завершён без ошибок; остались предупреждения существующих Android binding, nullable и устаревшего Camera API. Установка на устройство и VPN здесь не проверялись.
- Ubuntu Desktop VM и компьютер института в этом этапе не использовались. Системные сетевые проверки ещё не выполнялись.
