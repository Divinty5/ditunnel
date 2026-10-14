using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;
using Tmds.DBus.Protocol;

// A real private D-Bus, with deterministic service failures and no network changes.
internal sealed class LinuxClientRecoverySmoke : IPathMethodHandler
{
    public string Path => LinuxNetworkProtocol.Path;
    public bool HandlesChildPaths => false;
    private bool fail = true;
    private VpnConnectionState state = VpnConnectionState.Error;

    public static async Task RunAsync(CancellationToken token)
    {
        const string destination = "org.divinty5.DiTunnel.RecoveryTest";
        using var service = new DBusConnection(DBusAddress.Session!);
        await service.ConnectAsync();
        service.AddMethodHandler(new LinuxClientRecoverySmoke());
        await service.RequestNameAsync(destination);
        using var client = new LinuxNetworkClient(DBusAddress.Session, destination);
        var profile = new ImportedProfile("Synthetic", "VLESS", "vless://11111111-1111-1111-1111-111111111111@192.0.2.1:443?security=none");
        try { await client.ConnectAsync(profile, token); throw new Exception("Injected connection failure ignored"); }
        catch (InvalidOperationException) { }
        if (client.Status.State != VpnConnectionState.Error) throw new Exception("Cleanup failure not exposed");
        if (!client.IsNetworkProtectionActive) throw new Exception("Retained protection was not read after failed Connect");
        try { await client.ConnectAsync(profile, token); throw new Exception("Unclean session accepted a new connection"); }
        catch (InvalidOperationException error) when (error.Message.Contains("Восстановить сеть", StringComparison.Ordinal)) { }
        // Before the fix the cleanup catch disposed the connection permanently.
        await client.RestoreNetworkAsync(token);
        await client.ConnectAsync(profile, token);
        if (client.Status.State != VpnConnectionState.Connected) throw new Exception("Recovery retained a stale session");
        await client.DisconnectAsync(token);
        if (client.Status.State != VpnConnectionState.Disconnected || client.IsNetworkProtectionActive) throw new Exception("Disconnect not confirmed");
        Console.WriteLine("PASS_CLIENT_RECOVERS_AFTER_CONNECT_AND_CLEANUP_FAILURE_WITHOUT_RESTART");
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var member = context.Request.MemberAsString;
        if (fail && member is "Connect" or "Disconnect")
        {
            context.ReplyError("org.divinty5.DiTunnel.InjectedFailure", "Synthetic cleanup failure");
            return default;
        }
        if (member == "RestoreNetwork") { fail = false; state = VpnConnectionState.Disconnected; }
        if (member == "Connect") state = VpnConnectionState.Connected;
        if (member == "Disconnect") state = VpnConnectionState.Disconnected;
        if (member == "GetStatus")
        {
            using var writer = context.CreateReplyWriter("uxsb");
            writer.WriteUInt32((uint)state); writer.WriteInt64(0); writer.WriteString(""); writer.WriteBool(fail);
            context.Reply(writer.CreateMessage());
        }
        else { using var writer = context.CreateReplyWriter(null); context.Reply(writer.CreateMessage()); }
        return default;
    }
}
