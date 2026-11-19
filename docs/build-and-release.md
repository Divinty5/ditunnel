# Сборка и публикация

Для общего выпуска 0.6.18 номер каждой платформы задан в `Directory.Build.props`. Android versionCode 60022 задан в `src/DiTunnel.Android/DiTunnel.Android.csproj`. Версии SDK и нативных компонентов закреплены в `global.json` и `eng/`.

## Разработка и тесты

После установки SDK и workloads выполните `dotnet restore` и `dotnet test DiTunnel.sln`. На Windows `./scripts/Start-DiTunnel.ps1` подготавливает закреплённые нативные компоненты и запускает приложение из исходников. Для тестирования нативных частей есть `./scripts/Build-AmneziaWG.ps1 -Test` и `./scripts/Build-ShellIcon.ps1 -Test`.

## Windows

Нужны .NET SDK из global.json, C++ Build Tools x64 и CMake 3.21+ для ShellIcon, Go из manifests и **Inno Setup 6.7+**. Из корня PowerShell:

```powershell
./scripts/Build-WindowsInstaller.ps1
```

`-CompilerPath` задаёт путь ISCC.exe. Скрипт создаёт `artifacts/installer/Di-Tunnel-0.6.18-Setup-x64.exe` и `.exe.sha256`. Оформление и выбор языка определяются файлами `.iss` в `installer/`. Установщик выпускается без подписи Authenticode.

## Android

Нужны .NET Android workload, SDK API 36/build-tools, JDK, libXray AAR и native AWG из manifests. Публичная цель — `android-arm64`, минимум API 27. `scripts/Build-Android.ps1` принимает пути AndroidRoot/JDK. На релизной машине используется локальный workflow с дополнительными проверками подписи и упаковки; он и окружение находятся в игнорируемой `.tools/android-build-local` и не являются переносимой зависимостью репозитория.

В APK включаются `geoip.dat` и `geosite.dat` из Windows-архива Xray 26.3.27. CI скачивает архив по `eng/xray-android-geodata-version.json`, проверяет его SHA-256 и распаковывает в `.tools/xray/26.3.27/windows-x64/`.

Release APK подписывается локально существующим ключом. Пароли передаются через файлы. Для проверки готового APK запустите `scripts/Test-AndroidApk.ps1` на том же коммите, с которого собран пакет:

```powershell
./scripts/Test-AndroidApk.ps1 -Apk $releaseApk -BuildTools $androidBuildTools `
    -SigningCertificateSha256 $releaseCertificateSha256
```

`$androidBuildTools` указывает на каталог SDK `build-tools/36.0.0`, а `$releaseCertificateSha256` содержит отпечаток известного релизного сертификата. Скрипт проверяет подпись и сертификат, application ID, версию, versionCode, minSdk, targetSdk, ABI arm64-v8a, файлы runtime и выравнивание APK. Если нужно выровнять пакет, выполните `zipalign -P 16 4` на APK без подписи, затем подпишите полученный файл. Для проверки устройства через ADB нужен отдельный запуск.

## Linux

Сборка на Linux amd64, включая WSL; зависимости — в [инструкции](linux-installation.md).

```bash
python3 scripts/Build-LinuxPackage.py --test
```

`--runtime-directory` позволяет переиспользовать проверенный runtime. Тесты получают его через `DITUNNEL_TEST_XRAY`. Упаковка идёт во временной Linux-папке для сохранения Unix-прав на WSL. Скрипт создаёт `.deb`, графический установщик `.run` с тем же пакетом внутри и контрольные суммы в `artifacts/linux/release`. Путь можно задать через `--output` внутри репозитория, чтобы тесты находили `Directory.Build.props`. `Test-LinuxPackage.py` проверяет версию, SHA-256, ELF, права файлов, разрешённые D-Bus операции и синтаксис сценариев пакета. Для проверки встроенного пакета запустите `.run` с аргументом `--verify`.

## GitHub

Workflow **Platform packages** автоматически запускается при отправке тега `vX.Y.Z`. Он берёт исходники из коммита тега и запускает сборку для каждой платформы, чья версия в `Directory.Build.props` совпадает с тегом. Тег должен указывать на коммит, в котором уже есть workflow. Для проверки кандидата до тега используйте **Actions → Platform packages → Run workflow** на `main`, укажите версию и отметьте нужные платформы. Можно также запустить проверку на существующем теге через `gh workflow run platform-packages.yml --ref v0.6.18` и задать выбор через `-f windows=true -f android=false -f linux=false`.

Windows CI запускает общие и Windows-тесты, самопроверку ShellIcon и тесты Go, а также проверяет версию и SHA-256 установщика. Linux переиспользует **Linux release package**, включая тесты, проверки `.deb` и `.run`. Android запускает тесты политик VPN из Core, проверяет ARM64 APK с тестовой подписью и сохраняет его с суффиксом `-ci.apk`. Для публикации Android используйте локально подписанный APK и команду проверки выше. GitHub runners не требуют релизных ключей.

1. Обновить версии выбранных платформ, CHANGELOG, README и матрицы. Исторические отчёты сохранить.
2. Запустить сборку и проверки для выбранного коммита. Сверить `source-commit.txt` в артефактах с этим коммитом. Проверить имена и SHA-256 публикуемых файлов через `scripts/Test-ReleaseAssets.ps1`, передав только нужные параметры из `-WindowsInstaller`, `-AndroidApk`, `-LinuxDeb`, `-LinuxRun`.
3. Создать тег `v<версия>` на этом коммите, если его ещё нет. Создать черновик релиза с пакетами выбранных платформ и их `.sha256`. Проверить имена, размеры и SHA-256 загруженных файлов и опубликовать выпуск вручную.
4. Проверить публичные ссылки и выбор пакетов для обновлённых платформ. Номер в имени файла должен совпадать с тегом.
5. При изменении диагностики сверить короткое сообщение и поля отчёта на Windows, Android и Linux. Одинаковый этап и причина дают одинаковый текст. При повторной проверке отчёт должен соответствовать текущей попытке, в том числе при переходе от успеха к ошибке.

Пакеты включают runtime. Linux GUI и служба обновляются одним пакетом, версия протокола службы — 3. [Изменения выпуска](release-0.6.18.md).
