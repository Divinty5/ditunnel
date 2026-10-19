using System.Security.Cryptography;
using System.Text;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App;

// Preferences contain opaque identifiers, never server links or credentials.
public sealed record ServerSelection(string ContentHash, string IdentityHash)
{
    public static ServerSelection From(ImportedProfile profile) => new(
        Hash(profile.SourceId + "\n" + profile.Content),
        Hash(profile.SourceId + "\n" + profile.Kind + "\n" + profile.Name));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
