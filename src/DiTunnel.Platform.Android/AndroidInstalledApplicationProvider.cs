using Android.Content;
using Android.Content.PM;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Android;

public sealed class AndroidInstalledApplicationProvider(Context context) : IInstalledApplicationProvider
{
    private readonly Context context = context.ApplicationContext ?? context;

    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var manager = context.PackageManager ?? throw new InvalidOperationException("Менеджер приложений Android недоступен.");
        using var intent = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
        var applications = manager.QueryIntentActivities(intent, PackageInfoFlags.MatchAll)
            .Select(item => new InstalledApplication(item.ActivityInfo?.PackageName ?? "", item.LoadLabel(manager)?.ToString() ?? ""))
            .Where(item => item.Id.Length != 0 && !string.Equals(item.Id, context.PackageName, StringComparison.Ordinal))
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return (IReadOnlyList<InstalledApplication>)applications;
    }, cancellationToken);
}
