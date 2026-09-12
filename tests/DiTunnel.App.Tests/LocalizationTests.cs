using DiTunnel.App;

namespace DiTunnel.App.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void TranslationResourceLoads()
    {
        Assert.Equal("Сервер не ответил.", L.T("Сервер не ответил."));
    }
}
