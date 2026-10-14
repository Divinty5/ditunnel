using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;

namespace DiTunnel.App;

// GNOME's dock loads the desktop entry's themed icon instead of _NET_WM_ICON.
// A user icon plus an AppInfo rescan updates the existing themed texture.
internal sealed class LinuxDockIcon : IDisposable
{
    private sealed record Lease(byte[]? Original, string Hash, string? CreatedDesktopHash);
    private readonly string icon, desktop, leaseFile, theme;
    private readonly byte[]? original;
    private string? desktopHash, lastHash;

    internal LinuxDockIcon(string dataDirectory, string stateDirectory)
    {
        theme = Path.Combine(dataDirectory, "icons/hicolor");
        icon = Path.Combine(theme, "256x256/apps/di-tunnel.png");
        desktop = Path.Combine(dataDirectory, "applications/di-tunnel.desktop");
        leaseFile = Path.Combine(stateDirectory, "dock-icon-lease.json");
        Directory.CreateDirectory(stateDirectory);
        if (File.Exists(leaseFile))
        {
            if (new FileInfo(leaseFile).Length > 2 * 1024 * 1024) throw new IOException("Invalid dock icon lease");
            var previous = JsonSerializer.Deserialize<Lease>(File.ReadAllBytes(leaseFile)) ?? throw new IOException("Invalid dock icon lease");
            Restore(previous);
        }
        if (File.Exists(icon))
        {
            if (new FileInfo(icon).LinkTarget is not null) throw new IOException("Preserve a custom symlinked icon");
            if (new FileInfo(icon).Length > 512 * 1024) throw new IOException("User icon too large to preserve");
            original = File.ReadAllBytes(icon);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        if (!File.Exists(desktop))
        {
            const string installed = "/usr/share/applications/di-tunnel.desktop";
            // Development builds without an installed desktop entry use only the window icon.
            if (!File.Exists(installed)) throw new IOException("Installed desktop entry missing");
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            var bytes = File.ReadAllBytes(installed);
            using var output = new FileStream(desktop, FileMode.CreateNew, FileAccess.Write);
            output.Write(bytes);
            desktopHash = Hash(bytes);
        }
    }

    internal void Update(WindowIcon value)
    {
        using var stream = new MemoryStream();
        value.Save(stream);
        var bytes = stream.ToArray();
        var hash = Hash(bytes);
        if (lastHash == hash) return;
        AtomicWrite(leaseFile, JsonSerializer.SerializeToUtf8Bytes(new Lease(original, hash, desktopHash)));
        AtomicWrite(icon, bytes);
        lastHash = hash;
        NotifyTheme();
    }

    private void NotifyTheme()
    {
        Directory.SetLastWriteTimeUtc(theme, DateTime.UtcNow);
        // AppInfoMonitor triggers GNOME's icon-theme rescan even when the icon
        // directory was first created after the desktop session started.
        if (File.Exists(desktop) && new FileInfo(desktop).Length <= 64 * 1024)
        {
            if (new FileInfo(desktop).LinkTarget is null) AtomicWrite(desktop, File.ReadAllBytes(desktop));
            else File.SetLastWriteTimeUtc(desktop, DateTime.UtcNow);
        }
    }

    private void Restore(Lease lease)
    {
        // Preserve user edits made while the application was running.
        if (File.Exists(icon) && Hash(File.ReadAllBytes(icon)) == lease.Hash)
        {
            if (lease.Original is null) File.Delete(icon);
            else AtomicWrite(icon, lease.Original);
        }
        if (lease.CreatedDesktopHash is { } hash && File.Exists(desktop) && Hash(File.ReadAllBytes(desktop)) == hash)
            File.Delete(desktop);
        File.Delete(leaseFile);
        if (Directory.Exists(theme)) NotifyTheme();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (OperatingSystem.IsLinux() && File.Exists(path)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        if (lastHash is { } hash) Restore(new Lease(original, hash, desktopHash));
        else if (desktopHash is { } created && File.Exists(desktop) && Hash(File.ReadAllBytes(desktop)) == created) File.Delete(desktop);
    }
}
