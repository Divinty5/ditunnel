namespace DiTunnel.Core.Profiles;

public sealed class ProfileStoreUnavailableException(string message) : InvalidOperationException(message);

// Used for an unavailable platform store and the designer. Never falls back to
// plaintext or silently replaces an unreadable persistent store with an empty one.
public sealed class UnavailableProfileStore(string message) : IProfileStore
{
    public IReadOnlyList<ImportedProfile> Load() => throw new ProfileStoreUnavailableException(message);
    public void Save(IEnumerable<ImportedProfile> profiles) => throw new ProfileStoreUnavailableException(message);
}
