using System.Runtime.InteropServices;
using System.Text.Json;
using DiTunnel.Core;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App;

internal sealed class ProfileStorage : IProfileStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiTunnel", "profiles.dat");
    public IReadOnlyList<ImportedProfile> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize(Protect(File.ReadAllBytes(FilePath), false), DiTunnelJsonContext.Default.ImportedProfileArray) ?? [] : [];

    public void Save(IEnumerable<ImportedProfile> profiles)
    {
        var bytes = Protect(JsonSerializer.SerializeToUtf8Bytes(profiles.ToArray(), DiTunnelJsonContext.Default.ImportedProfileArray), true);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, FilePath, true);
    }

    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var success = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.Security.Cryptography.CryptographicException();
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
