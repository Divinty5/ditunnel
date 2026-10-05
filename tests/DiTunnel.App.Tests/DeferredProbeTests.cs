using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class DeferredProbeTests
{
    private sealed class Store : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Items = [new("AWG", "AmneziaWG", "awg", "sub", "Subscription"), new("Unavailable", "VMess", "vmess", "sub", "Subscription")];
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) => Items = profiles.ToArray();
    }
    private sealed class Probe : IServerProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
            => Task.FromResult(profile.Kind == "AmneziaWG"
                ? new ServerProbeResult(null, "AmneziaWG: проверка после подключения", IsDeferred: true)
                : new ServerProbeResult(null, "Таймаут"));
    }
    [Fact]
    public async Task UnavailableFilterKeepsServersThatNeedAnActiveTunnelToBeTested()
    {
        var store = new Store();
        var vm = new MainViewModel(null, store, probe: new Probe());
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.UnavailableProfileCount);
        Assert.False(vm.Profiles.Single(row => row.Name == "AWG").ProbeTimedOut);
        vm.RemoveUnavailableCommand.Execute(null);
        Assert.Equal("AWG", Assert.Single(store.Items).Name);
    }
}
