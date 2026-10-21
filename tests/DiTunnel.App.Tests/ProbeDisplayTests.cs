using DiTunnel.App.ViewModels;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class ProbeDisplayTests
{
    [Theory]
    [InlineData("TCP")] [InlineData("TLS")] [InlineData("HTTP")] [InlineData("HTTPS")]
    public void Successful_probe_keeps_actual_method_next_to_numeric_delay(string method)
    {
        var row = new ServerItemViewModel(new ImportedProfile("Test", "VLESS", "test"))
        { ProbeMilliseconds = 428.2, ProbeText = method + " · 428 мс" };
        Assert.Equal("428 мс · " + method, row.ProbeDisplayText);
    }
    [Fact]
    public void Successful_probe_shows_number_first_and_keeps_detailed_result()
    {
        var row = new ServerItemViewModel(new ImportedProfile("Test", "VLESS", "test"));
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.ProbeText = "Сервер ответил через туннель за 428 мс";
        row.ProbeMilliseconds = 428.2;
        Assert.Equal("428 мс", row.ProbeDisplayText);
        Assert.Contains("через туннель", row.ProbeText);
        Assert.Contains(nameof(row.ProbeDisplayText), changed);
        row.ProbeText = "Проверяем…";
        Assert.Equal("Проверяем…", row.ProbeDisplayText);
        row.ProbeMilliseconds = null;
        row.ProbeText = "Нет ответа";
        Assert.Equal("Нет ответа", row.ProbeDisplayText);
    }
}
