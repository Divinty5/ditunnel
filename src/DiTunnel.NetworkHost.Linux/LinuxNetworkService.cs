using DiTunnel.Core.Connection;
using DiTunnel.Platform.Linux.Network;
using Tmds.DBus.Protocol;
using System.Text;

namespace DiTunnel.NetworkHost.Linux;

internal sealed class LinuxNetworkService(LinuxProbeCoordinator coordinator, LinuxProbeAuthorization authorization, LinuxVpnMethods? vpn = null, bool supportsSplit = true) : IPathMethodHandler
{
    private readonly SemaphoreSlim admission = new(8);
    public string Path => LinuxNetworkProtocol.Path;
    public bool HandlesChildPaths => false;
    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="org.divinty5.DiTunnel.Network1">
          <method name="GetCapabilities"><arg direction="out" type="u" name="version"/><arg direction="out" type="b" name="probe"/><arg direction="out" type="b" name="connect"/><arg direction="out" type="b" name="killSwitch"/><arg direction="out" type="b" name="splitTunnel"/></method>
          <method name="Probe"><arg direction="in" type="u" name="version"/><arg direction="in" type="s" name="id"/><arg direction="in" type="s" name="profile"/><arg direction="in" type="u" name="mode"/><arg direction="out" type="b" name="success"/><arg direction="out" type="d" name="milliseconds"/><arg direction="out" type="s" name="message"/><arg direction="out" type="b" name="deferred"/></method>
          <method name="CancelProbe"><arg direction="in" type="s" name="id"/></method>
          <method name="Connect"><arg direction="in" type="u" name="version"/><arg direction="in" type="s" name="id"/><arg direction="in" type="s" name="profile"/><arg direction="in" type="s" name="policy"/></method>
          <method name="Disconnect"><arg direction="in" type="s" name="id"/></method>
          <method name="GetStatus"><arg direction="out" type="u" name="state"/><arg direction="out" type="x" name="connectedAt"/><arg direction="out" type="s" name="message"/><arg direction="out" type="b" name="protectionActive"/></method>
          <method name="RestoreNetwork"/>
        </interface>
        """u8.ToArray();

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest) { context.ReplyIntrospectXml([InterfaceXml]); return default; }
        var request = context.Request;
        if (request.InterfaceAsString != LinuxNetworkProtocol.Interface) return default;
        if ((request.MemberAsString, request.SignatureAsString) == ("GetCapabilities", ""))
        {
            using var writer = context.CreateReplyWriter("ubbbb");
            writer.WriteUInt32(LinuxNetworkProtocol.Version);
            writer.WriteBool(true);
            writer.WriteBool(vpn is not null);
            writer.WriteBool(vpn is not null && File.Exists("/usr/sbin/nft"));
            writer.WriteBool(vpn is not null && supportsSplit);
            context.Reply(writer.CreateMessage());
            return default;
        }
        if (vpn?.Handle(context) == true) return default;
        if (request.MemberAsString == "CancelProbe" && request.SignatureAsString == "s")
        {
            try
            {
                var value = request.GetBodyReader().ReadStringAsSpan();
                if (value.Length != 36 || !Guid.TryParseExact(Encoding.UTF8.GetString(value), "D", out var id) || id == Guid.Empty)
                    throw new ArgumentException();
                // Cancellation does not need a new authorization and is scoped to the bus sender.
                context.DisposesAsynchronously = true;
                _ = CancelAsync(context, request.SenderAsString!, id);
            }
            catch { context.ReplyError("org.divinty5.DiTunnel.InvalidRequest", "Некорректный идентификатор."); }
            return default;
        }
        if (request.MemberAsString != "Probe" || request.SignatureAsString != "ussu") return default;
        if (!admission.Wait(0)) { ReplyProbe(context, new(null, "Сетевая служба занята.", IsDeferred: true)); return default; }
        try
        {
            var reader = request.GetBodyReader();
            var version = reader.ReadUInt32();
            var id = reader.ReadStringAsSpan();
            if (id.Length != 36 || !Guid.TryParseExact(Encoding.UTF8.GetString(id), "D", out var parsedId) || parsedId == Guid.Empty)
                throw new ArgumentException();
            var contentBytes = reader.ReadStringAsSpan();
            if (contentBytes.Length > LinuxNetworkProtocol.MaximumProfileBytes) throw new ArgumentException();
            var content = new UTF8Encoding(false, true).GetString(contentBytes);
            var mode = reader.ReadUInt32();
            var profile = LinuxNetworkProtocol.ParseProfile(version, content, mode);
            context.DisposesAsynchronously = true;
            _ = ProbeAsync(context, request.SenderAsString!, parsedId, profile, (ServerProbeMode)mode);
            return default;
        }
        catch
        {
            admission.Release();
            context.ReplyError("org.divinty5.DiTunnel.InvalidRequest", "Проверка принимает один поддерживаемый профиль и версию протокола 2.");
            return default;
        }
    }

    private async Task ProbeAsync(MethodContext context, string sender, Guid id, DiTunnel.Core.Profiles.ImportedProfile profile, ServerProbeMode mode)
    {
        try
        {
            var result = await coordinator.ProbeAsync(sender, id, profile, mode, context.RequestAborted,
                token => authorization.AuthorizeAsync(sender, token)).ConfigureAwait(false);
            ReplyProbe(context, result);
        }
        catch (OperationCanceledException) { context.ReplyError("org.divinty5.DiTunnel.Cancelled", "Проверка отменена."); }
        catch { context.ReplyError("org.divinty5.DiTunnel.ProbeFailed", "Не удалось проверить сервер."); }
        finally { admission.Release(); context.Dispose(); }
    }

    private async Task CancelAsync(MethodContext context, string sender, Guid id)
    {
        try
        {
            await coordinator.CancelAsync(sender, id).ConfigureAwait(false);
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
        catch { context.ReplyError("org.divinty5.DiTunnel.CancelFailed", "Не удалось завершить проверку."); }
        finally { context.Dispose(); }
    }

    private static void ReplyProbe(MethodContext context, ServerProbeResult result)
    {
        using var writer = context.CreateReplyWriter("bdsb");
        writer.WriteBool(result.Milliseconds is not null);
        writer.WriteDouble(result.Milliseconds ?? 0);
        writer.WriteString(result.Message);
        writer.WriteBool(result.IsDeferred);
        context.Reply(writer.CreateMessage());
    }
}
