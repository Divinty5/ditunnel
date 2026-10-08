using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Linux.Network;

// Only bounded settings cross IPC; neither nft scripts nor Xray JSON come from the UI.
public sealed record LinuxVpnPolicy(SplitTunnelMode Mode, string[] Domains, string[] Processes,
    bool KillSwitch, bool AllowLan, bool BlockAds = false, bool StrictAds = false)
{
    public static LinuxVpnPolicy Default { get; } = new(SplitTunnelMode.ProxyAll, [], [], false, false);
    public const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    [JsonIgnore]
    public SplitTunnelPolicy Split => new(Mode, Domains, Processes);
    public string Serialize()
    {
        Validate();
        var value = JsonSerializer.Serialize(this, Json);
        if (Encoding.UTF8.GetByteCount(value) > MaximumBytes) throw new ArgumentException("Настройки Linux слишком велики.");
        return value;
    }
    public static LinuxVpnPolicy Parse(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumBytes) throw new ArgumentException("Настройки Linux слишком велики.");
        var result = JsonSerializer.Deserialize<LinuxVpnPolicy>(value, Json) ?? throw new ArgumentException();
        result.Validate(); return result;
    }
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Domains is null || Processes is null || Domains.Length > 256 || Processes.Length > 256)
            throw new ArgumentException("Некорректные настройки Linux.");
        foreach (var value in Domains)
            if (string.IsNullOrWhiteSpace(value) || value.Length > 253 || value.Any(char.IsControl)
                || (!IPAddress.TryParse(value, out _) && Uri.CheckHostName(value) == UriHostNameType.Unknown))
                throw new ArgumentException("Некорректный домен или IP-адрес.");
        foreach (var value in Processes)
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !value.StartsWith('/') || value.EndsWith('/')
                || value.Any(char.IsControl) || value.Split('/').Any(part => part is "." or ".."))
                throw new ArgumentException("Приложение Linux должно быть задано абсолютным путём к исполняемому файлу.");
    }
}
