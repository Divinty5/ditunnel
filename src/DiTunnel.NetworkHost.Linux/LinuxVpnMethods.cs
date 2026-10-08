using System.Text;
using DiTunnel.Platform.Linux.Network;
using Tmds.DBus.Protocol;

namespace DiTunnel.NetworkHost.Linux;

internal sealed class LinuxVpnMethods(LinuxVpnCoordinator coordinator, LinuxProbeAuthorization authorization, Func<Task> recover)
{
    public bool Handle(MethodContext context)
    {
        var request = context.Request;
        var sender = request.SenderAsString!;
        if (request.MemberAsString == "GetStatus" && request.SignatureAsString == "")
        {
            var status = coordinator.StatusFor(sender);
            using var writer = context.CreateReplyWriter("uxsb");
            writer.WriteUInt32((uint)status.State);
            writer.WriteInt64(status.ConnectedAt?.ToUnixTimeSeconds() ?? 0);
            writer.WriteString(status.Message ?? "");
            writer.WriteBool(coordinator.ProtectionActive);
            context.Reply(writer.CreateMessage());
            return true;
        }
        if (request.MemberAsString == "RestoreNetwork" && request.SignatureAsString == "")
        {
            context.DisposesAsynchronously = true;
            _ = ReplyAsync(context, () => coordinator.RecoverAsync(sender,
                token => authorization.AuthorizeAsync(sender, LinuxNetworkProtocol.RecoverAction, true, token), recover));
            return true;
        }
        if (request.MemberAsString == "Disconnect" && request.SignatureAsString == "s")
        {
            try
            {
                var id = ReadId(request.GetBodyReader().ReadStringAsSpan());
                context.DisposesAsynchronously = true;
                _ = ReplyAsync(context, () => coordinator.DisconnectAsync(sender, id));
            }
            catch { Invalid(context); }
            return true;
        }
        if (request.MemberAsString != "Connect" || request.SignatureAsString != "usss") return false;
        try
        {
            var reader = request.GetBodyReader();
            var version = reader.ReadUInt32();
            var id = ReadId(reader.ReadStringAsSpan());
            var bytes = reader.ReadStringAsSpan();
            if (bytes.Length > LinuxNetworkProtocol.MaximumProfileBytes) throw new ArgumentException();
            var profile = LinuxNetworkProtocol.ParseProfile(version, new UTF8Encoding(false, true).GetString(bytes), 0);
            var settings = reader.ReadStringAsSpan();
            if (settings.Length > LinuxVpnPolicy.MaximumBytes) throw new ArgumentException();
            var policy = LinuxVpnPolicy.Parse(new UTF8Encoding(false, true).GetString(settings));
            context.DisposesAsynchronously = true;
            _ = ReplyAsync(context, () => coordinator.ConnectAsync(sender, id, profile,
                token => authorization.AuthorizeAsync(sender, LinuxNetworkProtocol.ConnectAction, true, token), policy));
        }
        catch { Invalid(context); }
        return true;
    }
    private static Guid ReadId(ReadOnlySpan<byte> value) => value.Length == 36 && Guid.TryParseExact(Encoding.UTF8.GetString(value), "D", out var id) && id != Guid.Empty
        ? id : throw new ArgumentException();
    private static void Invalid(MethodContext context) => context.ReplyError("org.divinty5.DiTunnel.InvalidRequest", "Некорректный запрос VPN.");
    private static async Task ReplyAsync(MethodContext context, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
        catch { context.ReplyError("org.divinty5.DiTunnel.VpnFailed", "Не удалось выполнить операцию VPN. Проверьте права и состояние сетевой службы."); }
        finally { context.Dispose(); }
    }
}
