using Avalonia.Controls;
using DiTunnel.App.ViewModels;
using DiTunnel.App.Views;

namespace DiTunnel.App;

public static class UpdateFlow
{
    public static Task<string> CheckAsync(ContentControl owner, MainViewModel vm, bool manual, Action? afterLaunch = null) =>
        CheckCoreAsync(vm, manual, UserSettings.Current, App.UpdateInstaller,
            () => ReleaseChecker.CheckAsync(), (release, canInstall) => Dialogs.AskUpdate(owner, release, canInstall), afterLaunch);

    internal static async Task<string> CheckCoreAsync(MainViewModel vm, bool manual, UserSettings settings, IUpdateInstaller? installer,
        Func<Task<ReleaseCheckResult>> check, Func<AppRelease, bool, Task<string?>> prompt, Action? afterLaunch = null)
    {
        if (installer is null) return "Автообновление недоступно на этой платформе.";
        if (manual) vm.Notice = "Проверяем обновления…";
        var result = await check();
        if (result.Release is not { } release) { if (manual) vm.Notice = result.Message; return result.Message; }
        string? action;
        bool canInstall = release.InstallerUrl is not null && release.ChecksumUrl is not null;
        try { action = settings.InstallUpdatesAutomatically && canInstall ? "install" : await prompt(release, canInstall); }
        catch { return vm.Notice = "Не удалось показать предложение обновления. Попробуйте позже."; }
        if (action == "later") return "Напомним об обновлении при следующем запуске.";
        if (action != "install" || !canInstall) return result.Message;
        string path;
        try
        {
            var progress = new Progress<int>(value => vm.Notice = $"Загрузка обновления: {value}%");
            path = await installer.DownloadAsync(release, progress);
        }
        catch { return vm.Notice = "Не удалось скачать обновление или проверить контрольную сумму."; }
        if (vm.IsBusy) return vm.Notice = "Дождитесь завершения текущей операции и повторите обновление.";
        vm.IsBusy = true;
        try
        {
            vm.Notice = "Обновление загружено. Отключаем VPN…";
            await vm.DisconnectForUpdateAsync();
            vm.Notice = "Запускаем установку обновления…";
            await installer.LaunchAsync(path);
            if (afterLaunch is not null) await vm.PrepareToCloseAsync(TimeSpan.FromSeconds(8));
        }
        catch (UpdateInstallException error) { return vm.Notice = error.Message; }
        catch { return vm.Notice = "Не удалось запустить обновление. Повторите установку из настроек."; }
        finally { vm.IsBusy = false; }
        try { afterLaunch?.Invoke(); } catch { }
        return "Установщик обновления запущен.";
    }
}

public sealed class UpdateInstallException(string message) : Exception(message);
