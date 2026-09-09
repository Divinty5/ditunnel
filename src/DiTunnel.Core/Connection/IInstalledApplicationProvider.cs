namespace DiTunnel.Core.Connection;

public sealed record InstalledApplication(string Id, string Name)
{
    public override string ToString() => Name;
}

public interface IInstalledApplicationProvider
{
    Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken cancellationToken = default);
}
