using Tmds.DBus.Protocol;

namespace DiTunnel.NetworkHost.Linux;

internal sealed class LinuxProbeAuthorization(DBusConnection connection, uint? developmentUid)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> authorizedClients = new();
    public void SenderDisconnected(string sender) => authorizedClients.TryRemove(sender, out _);
    public async Task<bool> AuthorizeAsync(string sender, CancellationToken token)
        => await AuthorizeAsync(sender, DiTunnel.Platform.Linux.Network.LinuxNetworkProtocol.ProbeAction, false, token).ConfigureAwait(false);

    public async Task<bool> AuthorizeAsync(string sender, string action, bool interactive, CancellationToken token)
    {
        if (!sender.StartsWith(':')) return false;
        var uid = await connection.CallMethodAsync(CreateCredentials(sender), static (message, _) =>
            message.GetBodyReader().ReadUInt32()).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (developmentUid is { } expected) return uid == expected;
        var control = interactive && action is DiTunnel.Platform.Linux.Network.LinuxNetworkProtocol.ConnectAction or DiTunnel.Platform.Linux.Network.LinuxNetworkProtocol.RecoverAction;
        if (control && authorizedClients.ContainsKey(sender)
            && await CheckAsync(sender, DiTunnel.Platform.Linux.Network.LinuxNetworkProtocol.ProbeAction, false, token).ConfigureAwait(false)) return true;
        var allowed = await CheckAsync(sender, action, interactive, token).ConfigureAwait(false);
        if (allowed && control)
        {
            if (authorizedClients.Count >= 128) authorizedClients.Clear();
            authorizedClients[sender] = 0;
        }
        return allowed;
    }

    private async Task<bool> CheckAsync(string sender, string action, bool interactive, CancellationToken token)
        => await connection.CallMethodAsync(CreateAuthorization(sender, action, interactive), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.AlignStruct();
            return reader.ReadBool();
        }).WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);

    private MessageBuffer CreateCredentials(string sender)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus", member: "GetConnectionUnixUser", signature: "s");
        writer.WriteString(sender);
        return writer.CreateMessage();
    }

    private MessageBuffer CreateAuthorization(string sender, string action, bool interactive)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.PolicyKit1", path: "/org/freedesktop/PolicyKit1/Authority",
            @interface: "org.freedesktop.PolicyKit1.Authority", member: "CheckAuthorization", signature: "(sa{sv})sa{ss}us");
        writer.WriteStructureStart();
        writer.WriteString("system-bus-name");
        var subject = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("name");
        writer.WriteVariant(VariantValue.String(sender));
        writer.WriteDictionaryEnd(subject);
        writer.WriteString(action);
        var details = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(details);
        writer.WriteUInt32(interactive ? 1u : 0u);
        writer.WriteString("");
        return writer.CreateMessage();
    }
}
