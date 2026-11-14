namespace DiTunnel.Core.Profiles;

public interface IProfileStore
{
    IReadOnlyList<ImportedProfile> Load();
    void Save(IEnumerable<ImportedProfile> profiles);

    ProfileLibrary LoadLibrary() => new(Load().ToArray(), []);

    void SaveLibrary(ProfileLibrary library)
    {
        if (library.EmptySubscriptions.Length != 0)
            throw new NotSupportedException("Хранилище не поддерживает пустые подписки.");
        Save(library.Profiles);
    }
}
