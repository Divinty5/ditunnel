using Avalonia.Controls;
using DiTunnel.App.ViewModels;
using DiTunnel.App.Views;

namespace DiTunnel.App;

public static class UpdateFlow
{
    public static async Task<string> CheckAsync(ContentControl owner, MainViewModel vm, bool manual, Action? afterLaunch = null)
    {
        if (App.UpdateInstaller is null) return "Автообновление недоступно на этой платформе.";
        if (manual) vm.Notice = "Проверяем обновления…";
        var result = await ReleaseChecker.CheckAsync();
        if (result.Release is not { } release) { if (manual) vm.Notice = result.Message; return result.Message; }
        var version = ReleaseChecker.FormatVersion(release.Version);
        if (!manual && UserSettings.Current.SkippedUpdateVersion == version) return result.Message;
        var action = await Dialogs.AskUpdate(owner, release, release.InstallerUrl is not null && release.ChecksumUrl is not null);
        if (action == "later") { UserSettings.Current.SkippedUpdateVersion = version; try { UserSettings.Current.Save(); } catch { } return "Обновление пропущено до следующей версии."; }
        if (action == "github")
        {
            try { await (TopLevel.GetTopLevel(owner)?.Launcher?.LaunchUriAsync(new Uri(release.PageUrl)) ?? Task.FromResult(false)); }
            catch { vm.Notice = "Не удалось открыть страницу релиза."; }
            return result.Message;
        }
        if (action != "install") return result.Message;
        try
        {
            var progress = new Progress<int>(value => vm.Notice = $"Загрузка обновления: {value}%");
            var path = await App.UpdateInstaller.DownloadAsync(release, progress);
            vm.Notice = "Обновление загружено. Отключаем VPN…";
            await vm.PrepareToCloseAsync(TimeSpan.FromSeconds(8));
            App.UpdateInstaller.Launch(path); afterLaunch?.Invoke();
            return "Установщик обновления запущен.";
        }
        catch { return vm.Notice = "Не удалось скачать или запустить обновление. Контрольная сумма и подключение не прошли проверку."; }
    }
}
