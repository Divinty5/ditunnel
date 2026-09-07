using System.Net;
using System.Text.Json;

namespace DiTunnel.App;

public static class ReleaseChecker
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };
    public static async Task<(string Message, string? Url)> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Divinty5/ditunnel/releases/latest");
            request.Headers.UserAgent.ParseAdd("DiTunnel/" + UserSettings.Version);
            using var response = await Client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return ("Опубликованных релизов пока нет.", null);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return ("Не удалось определить версию релиза.", null);
            return remote > Version.Parse(UserSettings.Version)
                ? ($"Доступна новая версия: {remote}", "https://github.com/Divinty5/ditunnel/releases/latest")
                : ("Установлена актуальная версия.", null);
        }
        catch { return ("Не удалось проверить обновления. Попробуйте позже.", null); }
    }
}
