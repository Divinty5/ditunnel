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
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void AndroidControlsAndDynamicErrorsContainNoRussian(string language)
    {
        var texts = new[]
        {
            "Интерфейс", "Автоматически скачивать и устанавливать обновления", "Открыть проект на GitHub", "Позже",
            "Напомним об обновлении при следующем запуске.",
            "Нет совместимого установщика или файла SHA-256. Откройте GitHub в настройках, в разделе «Обновления».",
            "Не удалось применить начальные настройки раздельного туннелирования.",
            "Наведите камеру на QR-код", "Сканирование QR-кода", "Подключение…",
            "VPN подключён · AmneziaWG", "VPN отключён", "Отключить", "Сначала выберите сервер.",
            "Серверы подписки", "Управление VPN", "Изменяем VPN-подключение…",
            "Состояние VPN и безопасное отключение Di-Tunnel",
            "AmneziaWG: проверка после подключения",
            "AmneziaWG: сначала предоставьте разрешение на VPN",
            "Не удалось запустить служебный процесс AmneziaWG.",
            "Служебный процесс AmneziaWG завершился.",
            "AmneziaWG: сервер не подтвердил handshake за 18 секунд.",
            "Отменить подключение", "Снять все галочки", "Выбор приложений очищен.",
            "Не удалось отключить VPN; повторите отключение.",
            "Android VPN не подтвердил запуск за 30 секунд. (AwgRuntime)",
            "AmneziaWG: не удалось открыть защищённый UDP-транспорт. (AwgRuntime; InvalidOperationException)",
            "Не удалось импортировать: некорректная конфигурация VMess.",
            "Соединение потеряно. Попытка 3 из 10 через 15 с…",
            "Запускаем Android VPN для сервера Node-A…",
            "Ресурс Xray geosite.dat отсутствует.",
            "SOCKS-соединение не установлено (5)."
        };
        foreach (var text in texts)
            Assert.DoesNotMatch("[А-Яа-яЁё]", L.Translate(text, language));
        Assert.Contains("Node-A", L.Translate(texts[^3], language));
        Assert.Contains("geosite.dat", L.Translate(texts[^2], language));
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
