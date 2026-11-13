# Сборка и публикация

Последний релиз Windows — 0.6.11. Android и Linux выпускаются отдельно и сейчас имеют версию 0.6.10. Версии платформ задаются в `Directory.Build.props`, Android versionCode 60010 — в `src/DiTunnel.Android/DiTunnel.Android.csproj`. Версии SDK и нативных компонентов закреплены в `global.json` и `eng/`.

## Windows

Нужны .NET SDK, C++ Build Tools для ShellIcon, Go из manifests и **Inno Setup 6.7+**. Из корня PowerShell:

```powershell
./scripts/Build-WindowsInstaller.ps1
```

`-CompilerPath` задаёт путь ISCC.exe. Скрипт создаёт `artifacts/installer/Di-Tunnel-0.6.11-Setup-x64.exe` и `.exe.sha256`. Оформление и выбор языка определяются файлами `.iss` в `installer/`. Установщик выпускается без подписи Authenticode.

## Android

Нужны .NET Android workload, SDK API 36/build-tools, JDK, libXray AAR и native AWG из manifests. Публичная цель — `android-arm64`, минимум API 27. `scripts/Build-Android.ps1` принимает пути AndroidRoot/JDK. На релизной машине используется локальный workflow с дополнительными проверками подписи и упаковки; он и окружение находятся в игнорируемой `.tools/android-build-local` и не являются переносимой зависимостью репозитория.

Для воспроизведения APK 0.6.10 файлы `geoip.dat` и `geosite.dat` берутся из Windows-архива Xray 26.3.27 и читаются из `.tools/xray/26.3.27/windows-x64/`. SHA-256 архива закреплён в манифесте общего релиза. [Манифест 0.6.10](https://github.com/Divinty5/ditunnel/blob/v0.6.10/eng/xray-version.json).

Release APK подписывается существующим ключом вне Git. Пароли передаются apksigner через файлы. Проверяются сертификат, application ID, 0.6.10/60010, minSdk 27, единственная ABI arm64-v8a, native-библиотеки и `zipalign -c -P 16`. Workflow создаёт `artifacts/android/0.6.10/Di-Tunnel-0.6.10-arm64.apk` и `.apk.sha256`. ADB не требуется.

## Linux

Сборка на Linux amd64, включая WSL; зависимости — в [инструкции](linux-installation.md).

```bash
python3 scripts/Build-LinuxPackage.py --test
```

`--runtime-directory` позволяет переиспользовать проверенный runtime; тесты получают его через `DITUNNEL_TEST_XRAY`. Упаковка идёт во временной Linux-папке для сохранения Unix-прав на WSL. Read-only `Test-LinuxPackage.py` проверяет версию, SHA-256, ELF, владельцев/права, D-Bus whitelist и синтаксис maintainer scripts. Каталог по умолчанию — `artifacts/linux/release`; можно задать `--output`. Установка deb на основной системе для проверки упаковки не нужна.

## GitHub

1. Обновить версии, CHANGELOG, README и матрицы; исторические отчёты сохранить. Разделить изменения на смысловые коммиты с краткими русскими сообщениями.
2. Собрать пакеты из итогового commit, проверить подпись APK и SHA-256. Не включать реальные профили, ключи или журналы в commits/assets.
3. Создать тег `v<версия>` на этом commit и черновик релиза с пакетами обновлённых платформ и их `.sha256`. Проверить имена, размеры и SHA-256 загруженных файлов, затем опубликовать стабильный latest.
4. Проверить публичные ссылки и выбор assets для всех платформ. Номер в имени файла должен совпадать с тегом: установленный ReleaseChecker не угадывает отличающиеся версии.

Пакеты включают runtime. Linux GUI и служба обновляются одним пакетом, версия протокола службы — 3. [Изменения и проверки](release-0.6.10.md).
