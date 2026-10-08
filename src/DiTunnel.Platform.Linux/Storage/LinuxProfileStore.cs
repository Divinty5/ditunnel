using System.Security.Cryptography;
using System.Text.Json;
using DiTunnel.Core;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Storage;

public sealed class LinuxProfileStore : IProfileStore, IDisposable
{
    private static ReadOnlySpan<byte> Header => "DiTunnelProfiles\x01"u8;
    private const int NonceSize = 12, TagSize = 16, MaximumFileSize = 16 * 1024 * 1024;
    private readonly string filePath;
    private readonly byte[] key;
    private bool loaded, disposed;

    public LinuxProfileStore(string filePath, ReadOnlySpan<byte> key)
    {
        if (!Path.IsPathFullyQualified(filePath)) throw new ArgumentException("Требуется абсолютный путь.", nameof(filePath));
        if (key.Length != 32) throw new ArgumentException("Требуется ключ AES-256.", nameof(key));
        this.filePath = filePath;
        this.key = key.ToArray();
    }

    public IReadOnlyList<ImportedProfile> Load()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        loaded = false;
        byte[] bytes;
        try
        {
            using var file = File.OpenRead(filePath);
            if (file.Length < Header.Length + NonceSize + TagSize || file.Length > MaximumFileSize)
                throw new CryptographicException("Некорректный формат хранилища профилей.");
            bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            loaded = true;
            return [];
        }
        if (!bytes.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new CryptographicException("Неизвестный формат хранилища профилей.");
        var plaintext = new byte[bytes.Length - Header.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(bytes.AsSpan(Header.Length, NonceSize), bytes.AsSpan(Header.Length + NonceSize + TagSize),
                bytes.AsSpan(Header.Length + NonceSize, TagSize), plaintext, Header);
            var profiles = JsonSerializer.Deserialize(plaintext, DiTunnelJsonContext.Default.ImportedProfileArray)
                ?? throw new JsonException("Хранилище не содержит список профилей.");
            loaded = true;
            return profiles;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(IEnumerable<ImportedProfile> profiles)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!loaded) throw new InvalidOperationException("Сначала требуется успешно прочитать хранилище профилей.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profiles.ToArray(), DiTunnelJsonContext.Default.ImportedProfileArray);
        var temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plaintext.Length > MaximumFileSize - Header.Length - NonceSize - TagSize)
                throw new InvalidOperationException("Превышен размер хранилища профилей.");
            var bytes = new byte[Header.Length + NonceSize + TagSize + plaintext.Length];
            Header.CopyTo(bytes);
            RandomNumberGenerator.Fill(bytes.AsSpan(Header.Length, NonceSize));
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(bytes.AsSpan(Header.Length, NonceSize), plaintext, bytes.AsSpan(Header.Length + NonceSize + TagSize),
                    bytes.AsSpan(Header.Length + NonceSize, TagSize), Header);
            var directory = Path.GetDirectoryName(filePath)!;
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(directory);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(key);
    }
}
