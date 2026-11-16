#!/bin/sh
set -eu

package='@PACKAGE@'
checksum='@SHA256@'
version='@VERSION@'
title="Di-Tunnel $version"

case "${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}" in
    ru*)
        prompt="Установить Di-Tunnel $version? Система запросит пароль администратора."
        progress="Устанавливаем Di-Tunnel $version…"
        success="Di-Tunnel $version установлен. Его можно открыть из меню приложений."
        failure="Установка Di-Tunnel завершилась ошибкой."
        integrity="Не удалось проверить пакет Di-Tunnel."
        unsupported="Требуется Ubuntu 24.04 amd64 с Zenity и polkit. Либо установите deb-пакет через apt."
        ;;
    *)
        prompt="Install Di-Tunnel $version? The system will ask for administrator authentication."
        progress="Installing Di-Tunnel $version…"
        success="Di-Tunnel $version was installed. You can open it from the app menu."
        failure="Di-Tunnel installation failed."
        integrity="Could not verify the Di-Tunnel package."
        unsupported="Ubuntu 24.04 amd64 with Zenity and polkit is required. You can also install the deb package with apt."
        ;;
esac

error() {
    printf '%s\n' "$1" >&2
    if command -v zenity >/dev/null 2>&1; then
        zenity --error --title="$title" --no-markup --text="$1" || :
    fi
    exit 1
}

verify=false
case "${1:-}" in
    --verify) verify=true ;;
    "") ;;
    *) exit 2 ;;
esac

if ! "$verify"; then
    if ! command -v zenity >/dev/null 2>&1 || [ ! -x /usr/bin/pkexec ] ||
        [ ! -x /usr/bin/apt-get ] || [ "$(dpkg --print-architecture)" != amd64 ] || [ "$(id -u)" = 0 ]; then
        error "$unsupported"
    fi
    zenity --question --title="$title" --text="$prompt" || exit 0
fi

directory=$(mktemp -d /tmp/ditunnel-installer.XXXXXXXX)
trap 'rm -f -- "$directory/$package" "$directory/result"; rmdir -- "$directory"' EXIT
payload=$(awk '/^__DITUNNEL_DEB__$/ { print NR + 1; exit }' "$0")
if [ -z "$payload" ] || ! tail -n +"$payload" -- "$0" > "$directory/$package" ||
    ! (cd "$directory" && printf '%s  %s\n' "$checksum" "$package" | sha256sum --check --status); then
    if "$verify"; then exit 1; fi
    error "$integrity"
fi
if "$verify"; then exit 0; fi

(
    status=0
    /usr/bin/pkexec --disable-internal-agent /usr/bin/apt-get -o Dpkg::Use-Pty=0 \
        -o DPkg::Lock::Timeout=120 install --reinstall --yes -- "$directory/$package" >&2 || status=$?
    printf '%s\n' "$status" > "$directory/result"
    printf '100\n' || :
) | zenity --progress --pulsate --auto-close --no-cancel --title="$title" --text="$progress" || :

status=$(cat "$directory/result")
case "$status" in
    0)
        if [ "$(dpkg-query -W -f='${db:Status-Status} ${Version}' ditunnel:amd64)" != "installed $version" ]; then
            error "$failure"
        fi
        zenity --info --title="$title" --text="$success" || :
        ;;
    126) exit 0 ;;
    *) error "$failure (apt/polkit: $status)" ;;
esac
exit 0
__DITUNNEL_DEB__
