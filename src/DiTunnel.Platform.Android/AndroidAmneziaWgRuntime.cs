using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;

namespace DiTunnel.Platform.Android;

// Loaded exclusively in :awg. libXray must never be loaded into this process.
internal sealed class AndroidAmneziaWgRuntime : IDisposable
{
    private readonly Socket transport;
    private bool started;
    public int Port { get; }

    public AndroidAmneziaWgRuntime(string configurationJson, string probeAddress, string endpoint, Func<int, bool> protectSocket)
    {
        var name = global::Android.App.Application.ProcessName;
        var package = global::Android.App.Application.Context.PackageName;
        if (name != package + ":awg" && name != package + ":awgprobe")
            throw new InvalidOperationException("AmneziaWG должен запускаться в отдельном служебном процессе.");
        var address = IPAddress.Parse(endpoint);
        transport = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            transport.Bind(new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            var fd = checked((int)transport.SafeHandle.DangerousGetHandle());
            if (!protectSocket(fd)) throw new InvalidOperationException("AmneziaWG: Android не разрешил защитить UDP-сокет от маршрутизации в VPN.");
            var port = Start(configurationJson, probeAddress, fd);
            if (port < 1) throw new InvalidOperationException(port switch
            {
                -1 => "AmneziaWG: предыдущее ядро ещё запущено.",
                -2 => "AmneziaWG: некорректные параметры конфигурации.",
                -3 => "AmneziaWG: не удалось создать виртуальный сетевой стек.",
                -4 => "AmneziaWG: не удалось открыть защищённый UDP-транспорт.",
                -5 => "AmneziaWG: не удалось запустить локальный SOCKS-прокси.",
                -6 => "AmneziaWG: недоступен защищённый UDP-сокет.",
                -7 => "Ядро AmneziaWG отклонило конфигурацию. Проверьте параметры обфускации и версию протокола сервера.",
                _ => "AmneziaWG не запустил туннель. Проверьте параметры профиля."
            });
            started = true;
            Port = port;
        }
        catch { transport.Dispose(); throw; }
    }
    public bool HasHandshake => HasNativeHandshake() != 0;
    public void Dispose()
    {
        try { if (started) { Stop(); started = false; } }
        finally { transport.Dispose(); }
    }

    [DllImport("ditunnel-awg", EntryPoint = "DtAwgStart", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Start([MarshalAs(UnmanagedType.LPUTF8Str)] string configuration,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string probeAddress, int protectedFileDescriptor);
    [DllImport("ditunnel-awg", EntryPoint = "DtAwgHasHandshake", CallingConvention = CallingConvention.Cdecl)]
    private static extern int HasNativeHandshake();
    [DllImport("ditunnel-awg", EntryPoint = "DtAwgStop", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Stop();
}
