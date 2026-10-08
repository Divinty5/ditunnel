using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using DiTunnel.Platform.Linux.Network;
using DiTunnel.Platform.Linux.Desktop;
using Tmds.DBus.Protocol;

namespace DiTunnel.NetworkHost.Linux;

[SupportedOSPlatform("linux")]
internal static partial class Program
{
    [LibraryImport("libc")] private static partial uint geteuid();

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        try
        {
            var namespaceVpn = args.Length == 3 && args[0] == "--namespace-vpn-development";
            var development = namespaceVpn || args.Length == 3 && args[0] == "--session-development";
            var recoverOnly = args.Length == 1 && args[0] == "--recover-network";
            if (args.Length != 0 && !development && !recoverOnly) throw new ArgumentException();
            if (recoverOnly && geteuid() != 0) throw new UnauthorizedAccessException();
            // System service paths cannot be supplied by the UI or overridden by environment.
            var runtimeDirectory = development ? System.IO.Path.GetFullPath(args[1]) : "/usr/lib/ditunnel/runtime";
            var stateDirectory = development ? System.IO.Path.GetFullPath(args[2]) : "/run/ditunnel";
            if (recoverOnly && !Directory.Exists(stateDirectory))
            {
                await EnsureProtectionReleasedAsync();
                return 0;
            }
            if (!Directory.Exists(stateDirectory)) throw new DirectoryNotFoundException();
            using var hostLock = LinuxInstanceLock.TryAcquire(stateDirectory, "host.lock") ?? throw new InvalidOperationException();
            if (recoverOnly)
            {
                await LinuxVpnSession.RecoverAsync(stateDirectory, new LinuxNetworkCommands(), CancellationToken.None, releaseProtection: true);
                await EnsureProtectionReleasedAsync();
                Console.WriteLine("RECOVERED_DITUNNEL_NETWORK");
                return 0;
            }
            if (namespaceVpn && (geteuid() != 0 || new FileInfo("/proc/self/ns/net").LinkTarget == new FileInfo("/proc/1/ns/net").LinkTarget
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS"))
                || Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS") != Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
                throw new InvalidOperationException("Испытания VPN требуют отдельного namespace и отдельной шины DNS.");
            var address = development ? DBusAddress.Session : DBusAddress.System;
            using var connection = new DBusConnection(address ?? throw new InvalidOperationException());
            await connection.ConnectAsync();
            var commands = new LinuxNetworkCommands();
            var canConnect = namespaceVpn || !development && geteuid() == 0 && File.Exists("/dev/net/tun")
                && File.Exists("/usr/sbin/ip") && File.Exists("/usr/bin/resolvectl")
                && File.ReadAllText("/etc/resolv.conf").Contains("127.0.0.53", StringComparison.Ordinal);
            var recoveryFailed = false;
            var retainedProtection = false;
            if (canConnect)
            {
                await commands.ResolvedAsync(CancellationToken.None, "status");
                try { retainedProtection = await LinuxVpnSession.RecoverAsync(stateDirectory, commands, CancellationToken.None, releaseProtection: false); }
                catch { recoveryFailed = true; } // Keep the authorized recovery endpoint available.
            }
            await using var coordinator = new LinuxProbeCoordinator(new LinuxRuntimeServerProbe(runtimeDirectory, stateDirectory, canConnect ? LinuxRouteLease.Mark : 0));
            await using var vpn = new LinuxVpnCoordinator(() => new LinuxVpnSession(runtimeDirectory, stateDirectory, commands));
            if (recoveryFailed) vpn.RecordRecoveryFailure();
            if (retainedProtection) vpn.RecordRetainedProtection();
            var authorization = new LinuxProbeAuthorization(connection, development ? geteuid() : null);
            connection.AddMethodHandler(new LinuxNetworkService(coordinator, authorization, canConnect ? new LinuxVpnMethods(vpn, authorization,
                () => LinuxVpnSession.RecoverAsync(stateDirectory, commands, CancellationToken.None)) : null,
                supportsSplit: LinuxXrayProcessSupport.IsAvailable(runtimeDirectory)));
            using var disappeared = await connection.AddMatchAsync(new MatchRule
            {
                Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Type = MessageType.Signal
            }, static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Name: reader.ReadString(), OldOwner: reader.ReadString(), NewOwner: reader.ReadString());
            }, (Notification<(string Name, string OldOwner, string NewOwner)> notification) =>
            {
                if (notification.HasValue && notification.Value.Name.StartsWith(':') && notification.Value.NewOwner.Length == 0)
                    _ = coordinator.SenderDisconnectedAsync(notification.Value.Name);
                if (notification.HasValue && notification.Value.Name.StartsWith(':') && notification.Value.NewOwner.Length == 0)
                    _ = vpn.SenderDisconnectedAsync(notification.Value.Name);
            }, emitOnCapturedContext: false);
            await connection.RequestNameAsync(LinuxNetworkProtocol.Service);
            using var stopping = new CancellationTokenSource();
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopping.Cancel(); });
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopping.Cancel(); });
            Console.WriteLine(canConnect ? "READY_DITUNNEL_NETWORK1_VPN" : "READY_DITUNNEL_NETWORK1_PROBE_ONLY");
            await Task.WhenAny(connection.DisconnectedAsync(), Task.Delay(Timeout.Infinite, stopping.Token));
            return 0;
        }
        catch
        {
            // No profile data, runtime stderr, addresses, paths or tokens in the service journal.
            Console.Error.WriteLine("ERROR_DITUNNEL_NETWORK_HOST");
            return 1;
        }
    }

    private static async Task EnsureProtectionReleasedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tables = JsonNode.Parse(await new LinuxNetworkCommands().ReadAsync(deadline.Token))!["nftables"]!.AsArray();
        if (tables.Any(entry => entry?["table"]?["name"]?.ToString().StartsWith("dtks_", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("Сохранённый firewall требует восстановления владельца.");
    }
}
