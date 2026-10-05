using Android.Content;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

// The system starts Always-on without an Activity or an Intent containing credentials.
// Keep the last complete request in Keystore-encrypted, non-backup storage.
internal sealed class AndroidVpnResumeStore(Context context)
{
    private string PathFor => Path.Combine(context.NoBackupFilesDir?.AbsolutePath
        ?? throw new InvalidOperationException("Хранилище состояния Android VPN недоступно."), "vpn-resume.dat");

    public void Save(AndroidVpnServiceBridge.ServiceRequest request)
    {
        var temporary = PathFor + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, AndroidProfileStore.Encrypt(JsonSerializer.SerializeToUtf8Bytes(request, AndroidJsonContext.Default.ServiceRequest)));
            File.Move(temporary, PathFor, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public AndroidVpnServiceBridge.ServiceRequest? Load()
    {
        if (!File.Exists(PathFor)) return null;
        var request = JsonSerializer.Deserialize(AndroidProfileStore.Decrypt(File.ReadAllBytes(PathFor)), AndroidJsonContext.Default.ServiceRequest);
        // Deleting a profile must also prevent unattended reuse of its old credentials.
        return request is not null && new AndroidProfileStore(context).Load().Any(profile =>
            profile.SourceId == request.Profile.SourceId && profile.Content == request.Profile.Content) ? request : null;
    }
}
