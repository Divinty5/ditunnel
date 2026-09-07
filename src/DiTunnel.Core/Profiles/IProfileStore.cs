namespace DiTunnel.Core.Profiles;

public interface IProfileStore
{
    IReadOnlyList<ImportedProfile> Load();
    void Save(IEnumerable<ImportedProfile> profiles);
}
