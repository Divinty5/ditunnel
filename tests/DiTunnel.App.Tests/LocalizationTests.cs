using DiTunnel.App;

namespace DiTunnel.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationResourceLoads()
    {
        Assert.Equal("Сервер не ответил.", L.T("Сервер не ответил."));
    }

    [Theory]
    [InlineData("en", "Allow local network while the kill switch is active")]
    [InlineData("es", "Permitir la red local con el kill switch activo")]
    [InlineData("zh-Hans", "启用 kill switch 时允许访问局域网")]
    public void ProtectionSettingsAreTranslated(string language, string expected)
    {
        Assert.Equal(expected, L.Translate("Разрешать локальную сеть при активном kill switch", language));
        var source = "Сетевые изменения применяются автоматически при возврате на главный экран. Kill switch использует отдельные правила Windows Filtering Platform.";
        var translated = L.Translate(source, language);
        Assert.NotEqual(source, translated);
        Assert.DoesNotContain("Сетевые", translated);
        Assert.DoesNotContain("блокировать", L.Translate("Kill switch: блокировать трафик вне VPN", language));
    }

    [Fact]
    public void EveryCatalogEntryHasSpanishAndChineseTranslations()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("DiTunnel.App.Translations.json")!;
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var entry in document.RootElement.EnumerateObject())
            foreach (var language in new[] { "es", "zh-Hans" })
            {
                var translated = L.Translate(entry.Name, language);
                Assert.False(string.IsNullOrWhiteSpace(translated), entry.Name);
                Assert.NotEqual(entry.Name, translated);
            }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void SelectedLanguageSurvivesSettingsReload(string language)
    {
        var path = Path.Combine(Path.GetTempPath(), "DiTunnel-language-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Language = language }));
            Assert.Equal(language, UserSettings.Load(path).Language);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void StatusMessagesTranslateWithoutChangingServerNamesOrCounters(string language)
    {
        var translated = L.Translate("Node-A недоступен. Восстанавливаем VPN через Node-B…", language);
        Assert.Contains("Node-A", translated);
        Assert.Contains("Node-B", translated);
        Assert.DoesNotContain("недоступен", translated);
        translated = L.Translate("Проверены серверы: 12. Доступны: 9.", language);
        Assert.Contains("12", translated);
        Assert.Contains("9", translated);
        Assert.DoesNotContain("Доступны", translated);
    }
}
