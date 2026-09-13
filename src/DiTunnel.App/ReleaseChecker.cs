using System.Net;
using System.Text.Json;

namespace DiTunnel.App;

public sealed record AppRelease(Version Version, string PageUrl, string? InstallerUrl, string? ChecksumUrl);
public sealed record ReleaseCheckResult(string Message, AppRelease? Release = null);

public static class ReleaseChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Divinty5/ditunnel/releases/latest";
    private const string LatestReleasePage = "https://github.com/Divinty5/ditunnel/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<ReleaseCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("DiTunnel/" + UserSettings.Version);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new("Опубликованных релизов пока нет.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return Parse(json.RootElement, Version.Parse(UserSettings.Version), OperatingSystem.IsAndroid());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new("Не удалось проверить обновления. Попробуйте позже."); }
    }

    public static ReleaseCheckResult Parse(JsonElement release, Version currentVersion, bool android = false)
    {
        var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return new("Не удалось определить версию релиза.");
        if (remote <= currentVersion) return new("Установлена актуальная версия.");

        var pageUrl = release.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
        pageUrl = Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) && pageUri.Scheme == Uri.UriSchemeHttps
            && pageUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? pageUri.AbsoluteUri : LatestReleasePage;
        string? installerUrl = null;
        string? checksumUrl = null;
        var displayVersion = FormatVersion(remote);
        var installerName = android
            ? $"Di-Tunnel-{displayVersion}-arm64.apk"
            : $"Di-Tunnel-{displayVersion}-Setup-x64.exe";
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase)) installerUrl = uri.AbsoluteUri;
                if (string.Equals(name, installerName + ".sha256", StringComparison.OrdinalIgnoreCase)) checksumUrl = uri.AbsoluteUri;
            }
        }
        return new($"Доступна новая версия: {displayVersion}", new(remote, pageUrl, installerUrl, checksumUrl));
    }

    public static string FormatVersion(Version version) => version.Build >= 0 ? version.ToString(3) : version.ToString();
}

public interface IUpdateInstaller
{
    Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
    void Launch(string installerPath);
}
