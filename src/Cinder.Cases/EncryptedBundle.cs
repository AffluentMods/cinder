using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Cinder.Cases;

/// <summary>
/// Encrypted case bundle. Cinder ships cases as a single self-describing file.
///
/// <para><b>Format version 2</b> (written by this build) is a chunked AEAD stream, so a bundle
/// of any size is packed and unpacked in constant memory:</para>
/// <code>
///   header (16 bytes):   "CDRBUNDLE\0" + version(1)=2 + flags(1)=0 + reserved(4)=0
///   salt (32 bytes):     PBKDF2 salt
///   nonce prefix (7):    random, per bundle
///   chunk size (4 LE):   plaintext bytes per chunk (1 MiB)
///   chunks:              AES-256-GCM(plaintext chunk) || tag(16), repeated
/// </code>
/// <para>Chunk <c>i</c> is sealed with the nonce <c>prefix(7) || i (4, big-endian) || last(1)</c>
/// and with the 59 bytes above as associated data. The counter makes chunks impossible to
/// reorder or duplicate, the <c>last</c> byte (1 only on the final chunk) makes the stream
/// impossible to truncate or extend, and the associated data binds every chunk to this header.
/// The plaintext is a ZIP archive of the case directory. Every chunk but the last holds exactly
/// <c>chunk size</c> bytes, which is what lets a reader seek.</para>
///
/// <para><b>Format version 1</b> (one AES-GCM operation over the whole archive) is still read.
/// It could not be written or read above 2 GiB — a .NET array cannot be larger, and the format
/// needs ciphertext and plaintext each in one array — which is why it was replaced. Builds
/// older than this one cannot read version 2.</para>
///
/// Key derivation: PBKDF2-SHA256, 600,000 iterations (OWASP-recommended as of 2024). The
/// passphrase never leaves the user's process; for OS-managed keys see DPAPI/libsecret hooks.
/// </summary>
public static class EncryptedBundle
{
    private static readonly byte[] MagicPrefix = "CDRBUNDLE\0"u8.ToArray();
    private const int HeaderLen = 16;
    private const int VersionOffset = 10;
    private const byte VersionLegacy = 1;
    private const byte VersionChunked = 2;

    private const int Iterations = 600_000;
    private const int SaltLen = 32;
    private const int LegacyNonceLen = 12;
    private const int NoncePrefixLen = 7;
    private const int TagLen = 16;
    private const int KeyLen = 32;

    /// <summary>Plaintext bytes per chunk in bundles this build writes.</summary>
    internal const int DefaultChunkSize = 1024 * 1024;

    // Bounds on the chunk size a reader will accept. The field is authenticated, but it is read
    // before anything can be authenticated and it sizes two buffers, so it is bounded first.
    private const int MinChunkSize = 64;
    private const int MaxChunkSize = 64 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the total uncompressed size we'll extract from the inner ZIP. Defense against
    /// zip bombs (a 50 KB ZIP can decompress to petabytes). 32 GB matches the largest expected
    /// case bundle today.
    /// </summary>
    private const long MaxExtractedBytes = 32L * 1024 * 1024 * 1024;

    // ------------------------------------------------------------------------------------
    // Pack
    // ------------------------------------------------------------------------------------

    public static Task PackAsync(string sourceDir, string outputBundlePath, string passphrase, CancellationToken ct = default)
        => PackWithChunkSizeAsync(sourceDir, outputBundlePath, passphrase, DefaultChunkSize, ct);

    /// <summary>
    /// <see cref="PackAsync"/> with an explicit chunk size, so tests can make a small directory
    /// span many chunks.
    /// </summary>
    internal static async Task PackWithChunkSizeAsync(string sourceDir, string outputBundlePath, string passphrase, int chunkSize, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentException.ThrowIfNullOrEmpty(outputBundlePath);
        var sourceFull = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(sourceFull))
        {
            throw new DirectoryNotFoundException($"Case directory not found: {sourceFull}");
        }

        await SealAsync(outputBundlePath, passphrase, chunkSize, (sealedStream, partialPath, finalPath) =>
        {
            // The archive is written straight into the encrypting stream: no plaintext copy of
            // the case is staged on disk, in %TEMP% or anywhere else.
            using var zip = new ZipArchive(sealedStream, ZipArchiveMode.Create, leaveOpen: true);
            AddDirectory(zip, sourceFull, [partialPath, finalPath], ct);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Encrypts an already-built ZIP into the bundle framing. Split out of
    /// <see cref="PackAsync"/> so tests can seal a
    /// deliberately malformed archive — entry names that escape the destination, contents that
    /// inflate without bound — which packing a real directory would never produce.
    /// </summary>
    internal static Task EncryptZipAsync(string zipPath, string outputBundlePath, string passphrase, CancellationToken ct = default)
        => EncryptZipWithChunkSizeAsync(zipPath, outputBundlePath, passphrase, DefaultChunkSize, ct);

    internal static async Task EncryptZipWithChunkSizeAsync(string zipPath, string outputBundlePath, string passphrase, int chunkSize, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        await SealAsync(outputBundlePath, passphrase, chunkSize, (sealedStream, _, _) =>
        {
            using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = zip.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sealedStream.Write(buffer, 0, read);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a format-1 bundle. Kept only so the tests can prove that bundles made by earlier
    /// builds still open; nothing in the product writes this format any more.
    /// </summary>
    internal static async Task EncryptZipLegacyV1Async(string zipPath, string outputBundlePath, string passphrase, CancellationToken ct = default)
    {
        byte[]? key = null;
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(LegacyNonceLen);
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);

            plaintext = await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false);
            ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLen];
            using (var aes = new AesGcm(key, TagLen))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            await using var output = File.Create(outputBundlePath);
            await output.WriteAsync(BuildHeader(VersionLegacy), ct).ConfigureAwait(false);
            await output.WriteAsync(salt, ct).ConfigureAwait(false);
            await output.WriteAsync(nonce, ct).ConfigureAwait(false);
            await output.WriteAsync(BitConverter.GetBytes(TagLen), ct).ConfigureAwait(false);
            await output.WriteAsync(ciphertext, ct).ConfigureAwait(false);
            await output.WriteAsync(tag, ct).ConfigureAwait(false);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    /// <summary>
    /// Runs <paramref name="writePlaintext"/> against a stream that seals whatever is written to
    /// it into a format-2 bundle. The bundle is built under a temporary name next to its
    /// destination and renamed into place only once it is complete, so a failed or cancelled
    /// pack never leaves a truncated file where a bundle is expected. (Only ciphertext is ever
    /// written there.)
    /// </summary>
    private static async Task SealAsync(string outputBundlePath, string passphrase, int chunkSize,
        Action<Stream, string, string> writePlaintext, CancellationToken ct)
    {
        if (chunkSize is < MinChunkSize or > MaxChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        // GetDirectoryName of a bare file name is "" and CreateDirectory("") throws.
        var finalPath = Path.GetFullPath(outputBundlePath);
        var directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var partialPath = finalPath + ".partial-" + Guid.NewGuid().ToString("N");

        await Task.Run(() =>
        {
            byte[]? key = null;
            try
            {
                var salt = RandomNumberGenerator.GetBytes(SaltLen);
                var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixLen);
                key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
                var preamble = BuildPreamble(salt, noncePrefix, chunkSize);

                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    output.Write(preamble);
                    using var sealedStream = new ChunkWriter(output, key, noncePrefix, chunkSize, preamble, ct);
                    writePlaintext(sealedStream, partialPath, finalPath);
                    sealedStream.Complete();
                }
                File.Move(partialPath, finalPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(partialPath); } catch { }
                throw;
            }
            finally
            {
                // SECURITY: zero the key before GC. Best-effort — copies may survive elsewhere —
                // but it closes the obvious "leave the AES key sitting in RAM" window.
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds every file under <paramref name="sourceFull"/> (and every empty directory) to the
    /// archive, with forward-slash relative names. The bundle being written is skipped, so
    /// packing a directory into a file inside that same directory works.
    /// </summary>
    private static void AddDirectory(ZipArchive zip, string sourceFull, string[] exclude, CancellationToken ct)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var info in new DirectoryInfo(sourceFull).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceFull, info.FullName).Replace(Path.DirectorySeparatorChar, '/');

            if (info is DirectoryInfo dir)
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                {
                    zip.CreateEntry(relative + "/");
                }
                continue;
            }
            if (exclude.Any(x => string.Equals(x, info.FullName, comparison)))
            {
                continue;
            }
            zip.CreateEntryFromFile(info.FullName, relative, CompressionLevel.SmallestSize);
        }
    }

    // ------------------------------------------------------------------------------------
    // Unpack
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Decrypts a bundle (format 2 or 1) and extracts it under <paramref name="outputDir"/>.
    /// The whole bundle is authenticated before the first file is written: a wrong passphrase
    /// or a modified, truncated or reordered bundle produces no output at all.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a bundle, an unsupported version, or an
    /// archive entry that is unsafe to extract.</exception>
    /// <exception cref="AuthenticationTagMismatchException">Wrong passphrase, or the bundle
    /// has been altered.</exception>
    public static async Task UnpackAsync(string bundlePath, string outputDir, string passphrase, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        await Task.Run(() => Unpack(bundlePath, outputDir, passphrase, ct), ct).ConfigureAwait(false);
    }

    private static void Unpack(string bundlePath, string outputDir, string passphrase, CancellationToken ct)
    {
        using var input = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);

        var header = new byte[HeaderLen];
        ReadExactlyOrThrow(input, header, "Not a Cinder bundle.");
        if (!header.AsSpan(0, MagicPrefix.Length).SequenceEqual(MagicPrefix))
        {
            throw new InvalidDataException("Not a Cinder bundle.");
        }

        switch (header[VersionOffset])
        {
            case VersionChunked:
                UnpackChunked(input, header, outputDir, passphrase, ct);
                break;
            case VersionLegacy:
                UnpackLegacy(input, header, outputDir, passphrase, ct);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported bundle format version {header[VersionOffset]}; this build reads versions 1 and 2.");
        }
    }

    private static void UnpackChunked(FileStream input, byte[] header, string outputDir, string passphrase, CancellationToken ct)
    {
        var salt = new byte[SaltLen];
        var noncePrefix = new byte[NoncePrefixLen];
        var chunkSizeBytes = new byte[4];
        ReadExactlyOrThrow(input, salt, "Bundle truncated.");
        ReadExactlyOrThrow(input, noncePrefix, "Bundle truncated.");
        ReadExactlyOrThrow(input, chunkSizeBytes, "Bundle truncated.");

        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(chunkSizeBytes);
        if (chunkSize is < MinChunkSize or > MaxChunkSize)
        {
            throw new InvalidDataException($"Bundle declares an unsupported chunk size ({chunkSize}).");
        }

        byte[] preamble = [.. header, .. salt, .. noncePrefix, .. chunkSizeBytes];
        byte[]? key = null;
        try
        {
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
            using var plaintext = new ChunkReader(input, input.Position, key, noncePrefix, chunkSize, preamble);

            // Authenticate everything first. Extraction below re-reads chunks on demand; without
            // this pass a bundle corrupted half-way through would leave half a case on disk.
            plaintext.VerifyAll(ct);

            using var archive = new ZipArchive(plaintext, ZipArchiveMode.Read, leaveOpen: true);
            ExtractArchive(archive, outputDir, ct);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void UnpackLegacy(FileStream input, byte[] header, string outputDir, string passphrase, CancellationToken ct)
    {
        // Format 1 fixed the whole 16-byte header; anything else in it is not a v1 bundle.
        if (!header.AsSpan().SequenceEqual(BuildHeader(VersionLegacy)))
        {
            throw new InvalidDataException("Not a Cinder bundle.");
        }

        var salt = new byte[SaltLen];
        var nonce = new byte[LegacyNonceLen];
        var tagLenBuf = new byte[4];
        ReadExactlyOrThrow(input, salt, "Bundle truncated.");
        ReadExactlyOrThrow(input, nonce, "Bundle truncated.");
        ReadExactlyOrThrow(input, tagLenBuf, "Bundle truncated.");
        if (BitConverter.ToInt32(tagLenBuf) != TagLen) throw new InvalidDataException("Unsupported tag length.");

        var bodyLen = input.Length - input.Position - TagLen;
        if (bodyLen < 0) throw new InvalidDataException("Bundle truncated.");
        if (bodyLen > Array.MaxLength)
        {
            // Format 1 needs the ciphertext in one array. No build could have written one this
            // large either, so this is a malformed or hostile file rather than a lost bundle.
            throw new InvalidDataException(
                $"Format-1 bundle body is {bodyLen:N0} bytes; format 1 cannot exceed {Array.MaxLength:N0}.");
        }

        var ciphertext = new byte[bodyLen];
        var tag = new byte[TagLen];
        ReadExactlyOrThrow(input, ciphertext, "Bundle truncated.");
        ReadExactlyOrThrow(input, tag, "Bundle truncated.");

        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyLen);
            plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagLen))
            {
                try
                {
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new AuthenticationTagMismatchException(AuthenticationFailedMessage, ex);
                }
            }

            // Read the archive from memory: the decrypted case is not staged in %TEMP%.
            using var archive = new ZipArchive(new MemoryStream(plaintext, writable: false), ZipArchiveMode.Read);
            ExtractArchive(archive, outputDir, ct);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private const string AuthenticationFailedMessage =
        "Bundle authentication failed: the passphrase is wrong or the bundle has been modified.";

    /// <summary>
    /// Extracts an authenticated archive. SECURITY: entries are walked by hand so that
    ///   1. ZIP-slip — any entry whose resolved destination escapes the output directory — is refused;
    ///   2. the total decompressed size is capped on bytes actually written (zip-bomb defense);
    ///   3. entry names containing NUL or a drive / stream qualifier are refused.
    /// </summary>
    private static void ExtractArchive(ZipArchive archive, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        // A trailing separator on the caller's path ("C:\out\") used to become a doubled one in
        // the prefix below, and every entry was then refused as escaping the directory.
        var outputDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
        var prefix = Path.EndsInDirectorySeparator(outputDirFull)
            ? outputDirFull                                   // a root such as "C:\" or "/"
            : outputDirFull + Path.DirectorySeparatorChar;

        long extractedTotal = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
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
            if (!destination.StartsWith(prefix, StringComparison.Ordinal) && destination != outputDirFull)
            {
                throw new InvalidDataException($"Bundle entry escapes destination directory: {entry.FullName}");
            }

            // Directory entry?
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            // SECURITY: the cap has to be enforced on bytes actually written, not on
            // entry.Length. That field is the uncompressed size the archive *declares*, and
            // an attacker writes it — a bomb can claim a few hundred bytes per entry and
            // still inflate without limit. Checking the declared value first is a cheap
            // early reject; the real enforcement is the counting copy below.
            if (extractedTotal + entry.Length > MaxExtractedBytes)
            {
                throw new InvalidDataException(
                    $"Bundle declares over the {MaxExtractedBytes:N0}-byte extraction cap — possible zip bomb.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            extractedTotal += ExtractEntryBounded(entry, destination, MaxExtractedBytes - extractedTotal);
        }
    }

    /// <summary>
    /// Copies one archive entry to <paramref name="destination"/>, aborting if it produces more
    /// than <paramref name="remainingBudget"/> bytes, and returns how many were written.
    ///
    /// <para>A partially written file is deleted on abort so a failed extraction doesn't leave
    /// a truncated artifact behind that could be mistaken for evidence.</para>
    /// </summary>
    private static long ExtractEntryBounded(ZipArchiveEntry entry, string destination, long remainingBudget)
    {
        var written = 0L;
        try
        {
            using var source = entry.Open();
            using var target = File.Create(destination);
            var buffer = new byte[81920];
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                written += read;
                if (written > remainingBudget)
                {
                    throw new InvalidDataException(
                        $"Bundle entry '{entry.FullName}' exceeds the extraction cap — possible zip bomb.");
                }
                target.Write(buffer, 0, read);
            }
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
        return written;
    }

    // ------------------------------------------------------------------------------------
    // Framing
    // ------------------------------------------------------------------------------------

    private static byte[] BuildHeader(byte version)
    {
        var header = new byte[HeaderLen];
        MagicPrefix.CopyTo(header, 0);
        header[VersionOffset] = version;
        return header;
    }

    /// <summary>Everything before the first chunk of a format-2 bundle; also its associated data.</summary>
    private static byte[] BuildPreamble(byte[] salt, byte[] noncePrefix, int chunkSize)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, chunkSize);
        return [.. BuildHeader(VersionChunked), .. salt, .. noncePrefix, .. size];
    }

    private static void WriteNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint index, bool last)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixLen..], index);
        nonce[NoncePrefixLen + 4] = last ? (byte)1 : (byte)0;
    }

    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer, string message)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(message, ex);
        }
    }

    /// <summary>
    /// Write-only stream that seals what is written to it as format-2 chunks. A full buffer is
    /// held back until more data arrives, because only then is it known not to be the last
    /// chunk; <see cref="Complete"/> seals whatever remains with the <c>last</c> flag set.
    /// Disposing without completing leaves the output unterminated — and therefore
    /// unreadable — on purpose: an aborted pack must not look like a whole bundle.
    /// </summary>
    private sealed class ChunkWriter : Stream
    {
        private readonly Stream _output;
        private readonly AesGcm _aes;
        private readonly byte[] _noncePrefix;
        private readonly byte[] _associatedData;
        private readonly byte[] _plain;
        private readonly byte[] _cipher;
        private readonly CancellationToken _ct;
        private int _fill;
        private uint _index;
        private long _written;
        private bool _completed;

        public ChunkWriter(Stream output, byte[] key, byte[] noncePrefix, int chunkSize, byte[] associatedData, CancellationToken ct)
        {
            _output = output;
            _aes = new AesGcm(key, TagLen);
            _noncePrefix = noncePrefix;
            _associatedData = associatedData;
            _plain = new byte[chunkSize];
            _cipher = new byte[chunkSize];
            _ct = ct;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }   // a partial chunk cannot be flushed; Complete() does it
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            while (buffer.Length > 0)
            {
                if (_fill == _plain.Length)
                {
                    Emit(last: false);
                }
                var take = Math.Min(buffer.Length, _plain.Length - _fill);
                buffer[..take].CopyTo(_plain.AsSpan(_fill));
                _fill += take;
                _written += take;
                buffer = buffer[take..];
            }
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }
            Emit(last: true);
            _completed = true;
            _output.Flush();
        }

        private void Emit(bool last)
        {
            _ct.ThrowIfCancellationRequested();
            Span<byte> nonce = stackalloc byte[NoncePrefixLen + 5];
            Span<byte> tag = stackalloc byte[TagLen];
            WriteNonce(nonce, _noncePrefix, _index, last);
            _aes.Encrypt(nonce, _plain.AsSpan(0, _fill), _cipher.AsSpan(0, _fill), tag, _associatedData);
            _output.Write(_cipher, 0, _fill);
            _output.Write(tag);
            _fill = 0;
            if (!last)
            {
                // 2^32 chunks of 1 MiB is 4 PiB; running out means something is badly wrong, and
                // wrapping the counter would reuse a nonce.
                _index = checked(_index + 1);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_plain);
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Read-only, seekable view of a format-2 bundle's plaintext. Chunks are fixed-size, so
    /// plaintext offset <c>p</c> lives in chunk <c>p / chunkSize</c>; each chunk is decrypted
    /// and authenticated when first touched and one is kept cached. Seekability is what lets
    /// <see cref="ZipArchive"/> read the archive straight from the bundle without the decrypted
    /// case ever being written to disk or held in memory whole.
    /// </summary>
    private sealed class ChunkReader : Stream
    {
        private readonly FileStream _input;
        private readonly long _bodyStart;
        private readonly AesGcm _aes;
        private readonly byte[] _noncePrefix;
        private readonly byte[] _associatedData;
        private readonly int _chunkSize;
        private readonly long _chunkCount;
        private readonly int _lastStored;
        private readonly long _length;
        private readonly byte[] _stored;
        private readonly byte[] _plain;
        private long _cachedIndex = -1;
        private int _cachedLength;
        private long _position;

        public ChunkReader(FileStream input, long bodyStart, byte[] key, byte[] noncePrefix, int chunkSize, byte[] associatedData)
        {
            _input = input;
            _bodyStart = bodyStart;
            _noncePrefix = noncePrefix;
            _associatedData = associatedData;
            _chunkSize = chunkSize;

            long storedSize = chunkSize + TagLen;
            var bodyLength = input.Length - bodyStart;
            if (bodyLength < TagLen)
            {
                throw new InvalidDataException("Bundle truncated.");
            }
            _chunkCount = (bodyLength + storedSize - 1) / storedSize;
            var lastStored = bodyLength - (_chunkCount - 1) * storedSize;
            if (lastStored < TagLen || _chunkCount > uint.MaxValue)
            {
                throw new InvalidDataException("Bundle truncated.");
            }
            _lastStored = (int)lastStored;
            _length = (_chunkCount - 1) * chunkSize + (_lastStored - TagLen);

            _aes = new AesGcm(key, TagLen);
            _stored = new byte[storedSize];
            _plain = new byte[chunkSize];
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        /// <summary>Decrypts and authenticates every chunk, in order, without keeping any.</summary>
        public void VerifyAll(CancellationToken ct)
        {
            for (long i = 0; i < _chunkCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                Load(i);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _position >= _length)
            {
                return 0;
            }
            var index = _position / _chunkSize;
            var offset = (int)(_position % _chunkSize);
            if (index != _cachedIndex)
            {
                Load(index);
            }
            var take = Math.Min(buffer.Length, _cachedLength - offset);
            _plain.AsSpan(offset, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        private void Load(long index)
        {
            var last = index == _chunkCount - 1;
            var storedLength = last ? _lastStored : _chunkSize + TagLen;
            var plainLength = storedLength - TagLen;

            _cachedIndex = -1;
            _input.Position = _bodyStart + index * (_chunkSize + (long)TagLen);
            ReadExactlyOrThrow(_input, _stored.AsSpan(0, storedLength), "Bundle truncated.");

            Span<byte> nonce = stackalloc byte[NoncePrefixLen + 5];
            WriteNonce(nonce, _noncePrefix, (uint)index, last);
            try
            {
                _aes.Decrypt(nonce, _stored.AsSpan(0, plainLength), _stored.AsSpan(plainLength, TagLen),
                    _plain.AsSpan(0, plainLength), _associatedData);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new AuthenticationTagMismatchException(AuthenticationFailedMessage, ex);
            }
            _cachedIndex = index;
            _cachedLength = plainLength;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new IOException("Attempted to seek before the start of the bundle.");
            }
            _position = target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_plain);
                _aes.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
