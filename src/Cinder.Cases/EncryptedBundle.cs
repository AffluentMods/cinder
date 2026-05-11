using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Cinder.Cases;

/// <summary>
/// Encrypted case bundle. Cinder ships cases as a single self-describing file:
///
///   header (16 bytes):  "CDRBUNDLE\0" + version(1) + flags(1) + reserved(4)
///   salt (32 bytes):    PBKDF2 salt
///   nonce (12 bytes):   AES-GCM nonce
///   tag length (4 LE):  always 16
///   ciphertext:         AES-256-GCM(zip archive)
///   tag (16 bytes):     AES-GCM tag
///
/// Key derivation: PBKDF2-SHA256, 600,000 iterations (OWASP-recommended as of 2024). The
/// passphrase never leaves the user's process; for OS-managed keys see DPAPI/libsecret hooks.
/// </summary>
public static class EncryptedBundle
{
    private static readonly byte[] HeaderMagic = "CDRBUNDLE\0\x01\x00\x00\x00\x00\x00"u8.ToArray();
    private const int Iterations = 600_000;
    private const int SaltLen = 32;
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;

    public static async Task PackAsync(string sourceDir, string outputBundlePath, string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        Directory.CreateDirectory(Path.GetDirectoryName(outputBundlePath)!);

        // 1. Zip the source directory into a temp file.
        var zipPath = outputBundlePath + ".tmpzip";
        try
        {
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);

            // 2. Derive key, encrypt, write framed.
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            // SYSLIB0060 — Rfc2898DeriveBytes constructors are obsolete; use Pbkdf2.
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);

            var plaintext = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLen];
            using (var aes = new AesGcm(key, TagLen))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            await using var output = File.Create(outputBundlePath);
            await output.WriteAsync(HeaderMagic, ct).ConfigureAwait(false);
            await output.WriteAsync(salt, ct).ConfigureAwait(false);
            await output.WriteAsync(nonce, ct).ConfigureAwait(false);
            await output.WriteAsync(BitConverter.GetBytes(TagLen), ct).ConfigureAwait(false);
            await output.WriteAsync(ciphertext, ct).ConfigureAwait(false);
            await output.WriteAsync(tag, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    public static async Task UnpackAsync(string bundlePath, string outputDir, string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        Directory.CreateDirectory(outputDir);

        await using var input = File.OpenRead(bundlePath);
        var header = new byte[HeaderMagic.Length];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual(HeaderMagic))
        {
            throw new InvalidDataException("Not a Cinder bundle.");
        }
        var salt = new byte[SaltLen]; await input.ReadExactlyAsync(salt, ct).ConfigureAwait(false);
        var nonce = new byte[NonceLen]; await input.ReadExactlyAsync(nonce, ct).ConfigureAwait(false);
        var tagLenBuf = new byte[4]; await input.ReadExactlyAsync(tagLenBuf, ct).ConfigureAwait(false);
        var tagLen = BitConverter.ToInt32(tagLenBuf);
        if (tagLen != TagLen) throw new InvalidDataException("Unsupported tag length.");

        var bodyLen = input.Length - input.Position - TagLen;
        if (bodyLen < 0) throw new InvalidDataException("Bundle truncated.");
        var ciphertext = new byte[bodyLen]; await input.ReadExactlyAsync(ciphertext, ct).ConfigureAwait(false);
        var tag = new byte[TagLen]; await input.ReadExactlyAsync(tag, ct).ConfigureAwait(false);

        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, TagLen))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        var zipPath = Path.Combine(outputDir, "_bundle.zip");
        await File.WriteAllBytesAsync(zipPath, plaintext, ct).ConfigureAwait(false);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, outputDir, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }
}
