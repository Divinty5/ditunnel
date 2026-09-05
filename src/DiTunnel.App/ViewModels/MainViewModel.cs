using DiTunnel.Core.Connection;

namespace DiTunnel.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public string ProductName => "Di-Tunnel";

    public string StatusText => "Отключено";

    public string StageText => "Каркас приложения готов. Следующий этап — подключение Xray-core.";

    public VpnConnectionState State => VpnConnectionState.Disconnected;
}
