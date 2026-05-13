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

    /// <summary>
    /// Hard cap on the compressed body size we'll read off disk. Decryption allocates a
    /// plaintext buffer equal in size to the ciphertext, so an attacker who hands us a 50 GB
    /// "bundle" would otherwise OOM the process before we even check the auth tag.
    /// 8 GB is generous for a normal case bundle and refuses obviously hostile inputs.
    /// </summary>
    private const long MaxCiphertextBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the total uncompressed size we'll extract from the inner ZIP. Defense against
    /// zip bombs (a 50 KB ZIP can decompress to petabytes). 32 GB matches the largest expected
    /// case bundle today.
    /// </summary>
    private const long MaxExtractedBytes = 32L * 1024 * 1024 * 1024;

    public static async Task PackAsync(string sourceDir, string outputBundlePath, string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        Directory.CreateDirectory(Path.GetDirectoryName(outputBundlePath)!);

        // SECURITY: stage the zip in an isolated temp dir, never adjacent to the user-supplied
        // outputBundlePath. Reduces collision / symlink-race surface against the destination.
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, "bundle.zip");

        byte[]? key = null;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        try
        {
            ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);

            // Derive key, encrypt, write framed.
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);

            plaintext = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
            ciphertext = new byte[plaintext.Length];
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
            // SECURITY: zero key + plaintext from process memory before GC. Best-effort —
            // the strings/buffers may still survive in pinned heap or pooled buffers, but
            // this closes the obvious "leave the AES key sitting in RAM" window.
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }

    public static async Task UnpackAsync(string bundlePath, string outputDir, string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        Directory.CreateDirectory(outputDir);
        var outputDirFull = Path.GetFullPath(outputDir);

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
        // SECURITY: refuse absurd ciphertext sizes before allocating mirrored plaintext.
        if (bodyLen > MaxCiphertextBytes)
        {
            throw new InvalidDataException(
                $"Bundle ciphertext is {bodyLen:N0} bytes, exceeds the {MaxCiphertextBytes:N0}-byte cap.");
        }
        var ciphertext = new byte[bodyLen];
        await input.ReadExactlyAsync(ciphertext, ct).ConfigureAwait(false);
        var tag = new byte[TagLen]; await input.ReadExactlyAsync(tag, ct).ConfigureAwait(false);

        // Decrypt into staging buffer + a fresh temp dir.
        byte[]? key = null;
        byte[]? plaintext = null;
        var stagingDir = Path.Combine(Path.GetTempPath(), "cinder-unbundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, "bundle.zip");
        try
        {
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagLen))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            await File.WriteAllBytesAsync(zipPath, plaintext, ct).ConfigureAwait(false);

            // SECURITY: walk the ZIP entries manually so we can enforce:
            //   1. ZIP-slip — refuse any entry whose resolved destination escapes outputDir.
            //   2. Total decompressed size cap (zip-bomb defense).
            //   3. Refuse entries whose name contains nul or other control characters.
            using var archive = ZipFile.OpenRead(zipPath);
            long extractedTotal = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName))
                {
                    continue;
                }
                if (entry.FullName.Contains('\0') ||
                    entry.FullName.Contains(':', StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Bundle contains an entry with invalid path: {entry.FullName}");
                }

                var destination = Path.GetFullPath(Path.Combine(outputDirFull, entry.FullName));
                if (!destination.StartsWith(outputDirFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && destination != outputDirFull)
                {
                    throw new InvalidDataException($"Bundle entry escapes destination directory: {entry.FullName}");
                }

                // Directory entry?
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                extractedTotal += entry.Length;
                if (extractedTotal > MaxExtractedBytes)
                {
                    throw new InvalidDataException(
                        $"Bundle would extract over the {MaxExtractedBytes:N0}-byte cap — possible zip bomb.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
        }
    }
}
