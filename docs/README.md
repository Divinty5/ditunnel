# Документация Di-Tunnel

Актуальное состояние — общий [релиз 0.6.9](release-0.6.9.md) от 9 ноября 2026: Windows x64, Android ARM64 0.6.9/60009, Ubuntu 24.04 amd64. Отчёты с номером версии или датой относятся именно к той сборке и не подтверждают повторение всей матрицы в текущем релизе.

## Действующие инструкции

- [Установка и возможности](../README.md), [история изменений](../CHANGELOG.md), [безопасность](../SECURITY.md).
- [Сборка и публикация](build-and-release.md), [установка Linux](linux-installation.md).
- [Проверки Android](android-release-validation.md), [протоколы и управление](android-controls-and-protocols.md), [проверки Windows](windows-tun-validation.md).
- [Границы AWG](amneziawg-support-review.md), [интеграция](amneziawg-integration.md), [ручной сценарий](awg-manual-validation.md).
- [План проекта](development-plan.md), [план Android](android-development-plan.md), [план Linux](linux-development-plan.md). PDF Android-плана — исторический экспорт; актуальный статус ведётся в Markdown.

## Архитектура и системные проверки

- ADR: [Xray](adr/0001-xray-runtime.md), [Windows kill switch](adr/0002-windows-kill-switch.md), [Linux](adr/0003-linux-platform-boundaries.md).
- Linux: [защита](linux-protection-stage.md), [системный VPN](linux-vpn-stage.md), [runtime](linux-runtime-stage.md), [VM](linux-vm-testing.md), [первый этап](linux-first-stage.md).
- Интерфейс: [значок Windows](windows-taskbar-icon-investigation.md), [проверки UI](ui-updates-validation.md).

## История

- [Android 0.6.5](release-0.6.5-android.md), [Android 0.6.6](release-0.6.6-android.md), [Windows 0.6.8 / Android и Linux 0.6.7](release-0.6.8-windows-0.6.7-mobile-linux.md), [Windows 0.6.7](windows-0.6.7-test.md).
- Общие релизы: [0.6.4](release-0.6.4.md), [0.6.3](release-0.6.3.md), [0.6.2](release-0.6.2.md), [0.6.1](release-0.6.1.md), [0.5.13](release-0.5.13.md).
- Linux: [0.6.6](release-0.6.6-linux.md), [VM 0.6.6](linux-0.6.6-validation.md), [0.6.5](linux-0.6.5-validation.md), [0.6.0](release-0.6.0-linux.md).
- Другие прогоны: [Windows 0.6.5](windows-0.6.5-validation.md), [восстановление](controls-recovery-validation.md), [Android 0.5.20](android-0.5.20-api27-test.md), [0.5.21](android-0.5.21-network-validation.md), [0.5.22](android-0.5.22-probe-diagnostics.md), [обмен AWG 0.5.23](awg-sharing-0.5.23-test.md).
