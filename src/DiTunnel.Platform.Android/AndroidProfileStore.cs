using System.Buffers.Binary;
using System.Text.Json;
using Android.Content;
using Android.Security.Keystore;
using DiTunnel.Core.Profiles;
using DiTunnel.Core;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CryptographicException = System.Security.Cryptography.CryptographicException;

namespace DiTunnel.Platform.Android;

public sealed class AndroidProfileStore(Context context) : IProfileStore
{
    private const string KeyAlias = "com.divintyinteractive.ditunnel.profiles.v1";
    private const int HeaderSize = 8;
    private const int GcmTagBits = 128;
    private static ReadOnlySpan<byte> Magic => "DITP"u8;
    private readonly string filePath = Path.Combine(
        context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android не предоставил внутреннее хранилище без резервного копирования."),
        "profiles.dat");

    public IReadOnlyList<ImportedProfile> Load()
    {
        if (!File.Exists(filePath)) return [];

        var encrypted = File.ReadAllBytes(filePath);
        var json = Decrypt(encrypted);
        return JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ImportedProfileArray) ?? [];
    }

    public void Save(IEnumerable<ImportedProfile> profiles)
    {
        var encrypted = Encrypt(JsonSerializer.SerializeToUtf8Bytes(profiles.ToArray(), DiTunnelJsonContext.Default.ImportedProfileArray));
        var temporary = filePath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, filePath, true);
    }

    private static byte[] Encrypt(byte[] plaintext)
    {
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new CryptographicException("Android не поддерживает AES-GCM.");
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
        var nonce = cipher.GetIV() ?? throw new CryptographicException("Android Keystore не создал nonce.");
        var ciphertext = cipher.DoFinal(plaintext)
            ?? throw new CryptographicException("Android Keystore не зашифровал профили.");
        if (nonce.Length is 0 or > byte.MaxValue) throw new CryptographicException("Android Keystore вернул некорректный nonce.");

        var result = new byte[HeaderSize + nonce.Length + ciphertext.Length];
        Magic.CopyTo(result);
        result[4] = 1;
        result[5] = checked((byte)nonce.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), GcmTagBits);
        nonce.CopyTo(result, HeaderSize);
        ciphertext.CopyTo(result, HeaderSize + nonce.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] payload)
    {
        if (payload.Length < HeaderSize || !payload.AsSpan(0, 4).SequenceEqual(Magic) || payload[4] != 1)
            throw new CryptographicException("Файл профилей имеет неизвестный формат.");

        var nonceLength = payload[5];
        var tagBits = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6, 2));
        if (nonceLength == 0 || tagBits != GcmTagBits || payload.Length <= HeaderSize + nonceLength)
            throw new CryptographicException("Файл профилей повреждён.");

        var nonce = payload.AsSpan(HeaderSize, nonceLength).ToArray();
        var ciphertext = payload.AsSpan(HeaderSize + nonceLength).ToArray();
        using var parameters = new GCMParameterSpec(tagBits, nonce);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")
            ?? throw new CryptographicException("Android не поддерживает AES-GCM.");
        cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), parameters);
        return cipher.DoFinal(ciphertext)
            ?? throw new CryptographicException("Android Keystore не расшифровал профили.");
    }

    private static IKey GetOrCreateKey()
    {
        using var keyStore = KeyStore.GetInstance("AndroidKeyStore")
            ?? throw new CryptographicException("Android Keystore недоступен.");
        keyStore.Load(null);
        if (keyStore.GetKey(KeyAlias, null) is { } existing) return existing;

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")
            ?? throw new CryptographicException("Android Keystore не поддерживает генерацию AES-ключей.");
        using var specification = new KeyGenParameterSpec.Builder(
                KeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey()
            ?? throw new CryptographicException("Android Keystore не создал ключ профилей.");
    }
}
