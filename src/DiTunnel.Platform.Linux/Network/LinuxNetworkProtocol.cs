using System.Text;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Network;

public static class LinuxNetworkProtocol
{
    public const uint Version = 2;
    public const string Service = "org.divinty5.DiTunnel.Network1";
    public const string Path = "/org/divinty5/DiTunnel/Network1";
    public const string Interface = Service;
    public const string ProbeAction = "org.divinty5.ditunnel.probe";
    public const string ConnectAction = "org.divinty5.ditunnel.connect";
    public const string RecoverAction = "org.divinty5.ditunnel.recover";
    public const int MaximumProfileBytes = 2 * 1024 * 1024;
    public static TimeSpan ProbeTimeout => TimeSpan.FromSeconds(20);

    public static ImportedProfile ParseProfile(uint version, string content, uint mode)
    {
        if (version != Version) throw new NotSupportedException("Несовместимая версия сетевой службы.");
        if (mode > (uint)ServerProbeMode.Https || string.IsNullOrWhiteSpace(content)
            || content.Length > MaximumProfileBytes || Encoding.UTF8.GetByteCount(content) > MaximumProfileBytes)
            throw new ArgumentException("Некорректный запрос проверки.");
        var profiles = ProfileParser.Parse(content);
        if (profiles.Count != 1 || profiles[0].Kind == "Xray JSON")
            throw new ArgumentException("Проверка принимает только один поддерживаемый профиль.");
        // The service receives profile content, never subscription URLs or separate UI metadata.
        return profiles[0] with { Name = "Probe", SourceId = "probe", SourceName = "", SourceUrl = null, Usage = null };
    }
}

public sealed record LinuxNetworkCapabilities(uint ProtocolVersion, bool CanProbe, bool CanConnect, bool SupportsKillSwitch, bool SupportsSplitTunnel = false);
