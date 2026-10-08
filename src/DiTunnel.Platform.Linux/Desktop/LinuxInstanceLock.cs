using System.Runtime.Versioning;

namespace DiTunnel.Platform.Linux.Desktop;

[SupportedOSPlatform("linux")]
public sealed class LinuxInstanceLock : IDisposable
{
    private readonly FileStream stream;
    private LinuxInstanceLock(FileStream stream) => this.stream = stream;

    public static LinuxInstanceLock? TryAcquire(string dataDirectory, string fileName = "ui.lock")
    {
        if (fileName is not ("ui.lock" or "host.lock")) throw new ArgumentException(nameof(fileName));
        Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var stream = new FileStream(Path.Combine(dataDirectory, fileName), new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try { stream.Lock(0, 1); return new(stream); }
        catch (IOException) { stream.Dispose(); return null; }
    }

    public void Dispose() => stream.Dispose();
}
