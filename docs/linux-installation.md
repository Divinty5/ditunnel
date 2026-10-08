# Установка Di-Tunnel 0.6.0 для Linux

Первый установочный пакет предназначен для **Ubuntu 24.04 LTS, amd64**, с systemd, systemd-resolved и рабочим столом GNOME/XWayland либо X11. .NET, Xray и userspace AmneziaWG включены в пакет; Android Studio и .NET SDK для запуска не нужны. Linux имеет версию 0.6.0; Windows/Android остаются 0.5.19.

## Установка

Скачайте `Di-Tunnel-0.6.0-linux-amd64.deb` и соседний `.sha256` из одного релиза. В каталоге загрузки выполните:

```bash
sha256sum -c Di-Tunnel-0.6.0-linux-amd64.deb.sha256
sudo apt install ./Di-Tunnel-0.6.0-linux-amd64.deb
di-tunnel
```

Ярлык Di-Tunnel появляется в меню приложений. GUI запускается обычным пользователем; изменение сети разрешает polkit. Нужны доступная и разблокированная связка ключей Secret Service и системный DNS через systemd-resolved (`127.0.0.53`). Приложение не заменяет глобальный resolv.conf. На неподходящей конфигурации VPN недоступен, а профили не переписываются.

## Обновление и удаление

Перед ручным обновлением закройте GUI. Установите новый `.deb` той же командой `sudo apt install ./имя-пакета.deb`. Сценарии пакета останавливают службу и восстанавливают только объекты Di-Tunnel, включая сохранённый KS. После обновления запустите GUI заново. Проверка обновлений в приложении выбирает Linux asset с SHA-256; установка требует подтверждения системного администратора через polkit/apt.

```bash
sudo apt remove ditunnel
# Удалить также системную конфигурацию пакета:
sudo apt purge ditunnel
```

Пользовательские профили, настройки и ключ в связке ключей сохраняются при обновлении, remove и purge. Это позволяет переустановить программу без повторного импорта. Не удаляйте профильный файл и ключ по отдельности: восстановление требует обоих.

Если восстановление сети завершить не удалось, удаление/обновление останавливается до удаления runtime. Повторите восстановление через GUI либо, с остановленной службой:

```bash
sudo systemctl stop ditunnel-network
sudo /usr/lib/ditunnel/host/DiTunnel.NetworkHost.Linux --recover-network
sudo systemctl start ditunnel-network
```

## Автозапуск и рабочий стол

```bash
di-tunnel --enable-autostart
di-tunnel --disable-autostart
di-tunnel --version
```

Автозапуск открывает видимое окно после входа в графическую сессию. Он не включает VPN автоматически и не заменяет Always-on VPN. В GNOME трей может отсутствовать, поэтому окно не скрывается без возможности его вернуть; используйте обычное сворачивание в панель задач.

## Сборка

На Linux amd64 нужны .NET SDK из `global.json`, Python 3.12+, `dpkg-deb` и `desktop-file-validate` (`desktop-file-utils`). Из корня репозитория:

```bash
python3 scripts/Build-LinuxPackage.py --test
```

Команда подготавливает закреплённый runtime, запускает тесты, публикует UI/host с собственным .NET, собирает `.deb` и SHA-256 в `artifacts/linux/release`. Можно переиспользовать ранее собранный runtime через `--runtime-directory /путь/linux-x64`: бинарники и metadata проверяются перед упаковкой. Машинные пути и кэши в Git не записываются.

## Границы поддержки

РТ по доменам/IP и реальным executable, nftables KS, LAN, DNS и восстановление проверены в Ubuntu VM. При включённом KS смена профиля и сетевых настроек пока требует явного отключения и нового подключения. Защита до входа/после reboot, бесшовный reconnect, ARM64, универсальная поддержка Snap/Flatpak и полный протокольный паритет не заявлены. Реальный AWG handshake, публичный IPv6, suspend/uplink switch и институтская Ubuntu требуют отдельной проверки. Подробности: [отчёт VM](linux-vm-testing.md), [архитектура защиты](linux-protection-stage.md).

Порядок выполнения maintainer scripts соответствует [Debian Policy](https://www.debian.org/doc/debian-policy/ch-maintainerscripts.html). Установочные сценарии не удаляют чужие firewall tables и пользовательские каталоги.
