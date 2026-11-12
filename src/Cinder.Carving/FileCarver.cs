using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;

namespace Cinder.Carving;

/// <summary>
/// One carved object: where it starts in the source, how long it is, which rule found it and
/// whether it passed that rule's checks. <see cref="OutputPath"/> is set only when the object
/// was written; <see cref="Note"/> says how the length was decided or why nothing was written.
/// </summary>
public sealed record CarveHit(
    string Label,
    string Extension,
    long Offset,
    long Length,
    bool Validated,
    string? OutputPath = null,
    string? Note = null);

/// <summary>
/// Header-driven file carver.
///
/// <para><b>Scanning.</b> The input is read in 4 MiB windows with a retained tail, so a header
/// straddling a window boundary is still matched. Each window "owns" the objects that start
/// before its tail; later ones are left for the next window, which sees them at its head. That
/// partition is what keeps a boundary-straddling header from being carved twice, and the tail
/// is long enough that every owned hit has <see cref="HeadBytes"/> bytes in memory to inspect.</para>
///
/// <para><b>Deciding what a hit is worth.</b> A header pattern matching is only a candidate.
/// The rule's <see cref="CarveSignature.Probe"/> is run on the bytes already in the window: it
/// can discard the hit (the pattern occurred by chance), give the object's exact length from
/// the format's own header, or say the length is not recorded. Without a declared length the
/// footer is searched for; without a footer the object runs to the next header of the same
/// type or <see cref="UnsizedCarveLimitBytes"/>, whichever is nearer. Nothing here allocates or
/// reads in proportion to <see cref="CarveSignature.MaxLengthBytes"/>.</para>
///
/// <para><b>Bounded work.</b> Following a structure or searching for a footer costs reads in
/// proportion to the object, which is fine when there is an object. Evidence can be arranged
/// so that there never is: headers every few bytes, each leading into a long walk that ends
/// nowhere. Probes and footer searches therefore draw on one allowance that grows with the
/// amount of input scanned (<see cref="ScanWorkPerInputByte"/> bytes of work per byte of
/// input, plus <see cref="InitialScanWork"/>). When it is spent, further candidates are
/// reported as not extracted until the scan has advanced enough to refill it.</para>
///
/// <para><b>Extracting.</b> Objects are streamed to the output file through a fixed buffer,
/// not assembled in memory. Once an object's extent is known, further headers of the same type
/// inside it are skipped: they are that object's own content (the entries of a ZIP, the
/// thumbnail of a JPEG), not separate files.</para>
/// </summary>
public sealed class FileCarver
{
    private const int ChunkSize = 4 << 20;
    private const int CopyBufferSize = 1 << 20;

    /// <summary>Bytes guaranteed to be in memory at every hit (fewer only when the input ends first).</summary>
    public const int HeadBytes = 4096;

    /// <summary>Largest object a whole-blob <see cref="CarveSignature.Validator"/> is run on.</summary>
    public const int MaxValidatorBlobBytes = 256 << 20;

    /// <summary>Work probes and footer searches may do before any input has been scanned.</summary>
    public const long InitialScanWork = 256L << 20;

    /// <summary>Further work allowed per byte of input scanned.</summary>
    public const int ScanWorkPerInputByte = 8;

    private readonly IReadOnlyList<CarveSignature> _signatures;
    private readonly int _overlap;

    /// <summary>
    /// Ceiling on an object whose format gives neither a length nor a trailer. Such an object
    /// is carved to the next header of the same type or this many bytes, whichever is nearer
    /// (and never past the rule's own maximum).
    /// </summary>
    public long UnsizedCarveLimitBytes { get; init; } = 32L << 20;

    public FileCarver(IReadOnlyList<CarveSignature>? signatures = null)
    {
        _signatures = signatures ?? CarveSignatures.Defaults;
        if (_signatures.Count == 0)
        {
            throw new ArgumentException("At least one carve signature is required.", nameof(signatures));
        }
        foreach (var s in _signatures)
        {
            if (s.Header.Length == 0)
            {
                throw new ArgumentException($"Carve signature '{s.Label}' has an empty header pattern.", nameof(signatures));
            }
            if (s.HeaderOffset < 0 || s.FooterTrailerBytes < 0)
            {
                throw new ArgumentException($"Carve signature '{s.Label}' has a negative offset.", nameof(signatures));
            }
            if (s.MaxLengthBytes <= 0)
            {
                throw new ArgumentException($"Carve signature '{s.Label}' has no maximum length.", nameof(signatures));
            }
        }

        // The retained tail must hold a whole header pattern (so a straddling one is seen) and
        // a whole head (so a probe has its bytes), whichever is longer.
        var longestHeader = _signatures.Max(s => s.HeaderOffset + s.Header.Length);
        _overlap = Math.Max(HeadBytes, longestHeader) - 1;
    }

    /// <summary>Carves <paramref name="input"/> from its current position. Offsets are relative to that position.</summary>
    public IAsyncEnumerable<CarveHit> CarveAsync(
        Stream input,
        string? outputDirectory = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
        => CarveCoreAsync(input, 0, outputDirectory, progress, ct);

    /// <summary>
    /// Carves a slice of a larger source. <paramref name="baseOffset"/> is where the slice
    /// starts in that source; it is added to every reported <see cref="CarveHit.Offset"/> and
    /// used in the output file names, so two slices never write the same file.
    /// </summary>
    public IAsyncEnumerable<CarveHit> CarveAsync(
        Stream input,
        long baseOffset,
        string? outputDirectory,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
        => CarveCoreAsync(input, baseOffset, outputDirectory, progress, ct);

    private async IAsyncEnumerable<CarveHit> CarveCoreAsync(
        Stream input,
        long baseOffset,
        string? outputDirectory,
        IProgress<long>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // Everything below is relative to where the stream stood when carving began; a seekable
        // stream handed over part-way through is carved from there, not from its start.
        var seekable = input.CanSeek;
        var origin = seekable ? input.Position : 0;
        long? totalLength = seekable ? Math.Max(0, input.Length - origin) : null;

        var pool = ArrayPool<byte>.Shared;
        var window = pool.Rent(ChunkSize + _overlap);
        long windowStart = 0;
        var retained = 0;

        // Per rule: the end of the last object whose extent is known. Headers of the same
        // rule before that point are inside an object already carved.
        var carvedUntil = new Dictionary<CarveSignature, long>(ReferenceEqualityComparer.Instance);
        var budget = new ScanBudget { Remaining = InitialScanWork };

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = await ReadFullAsync(input, window.AsMemory(retained, ChunkSize), ct).ConfigureAwait(false);
                var len = retained + read;
                if (len == 0)
                {
                    yield break;
                }

                budget.Remaining += (long)ScanWorkPerInputByte * read;

                // A window that couldn't be filled is the last one, and owns its whole extent.
                var isLast = read < ChunkSize;
                var ownedEnd = isLast ? len : len - _overlap;

                foreach (var raw in ScanWindow(window, len, ownedEnd))
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = windowStart + raw.ObjectStart;
                    if (carvedUntil.TryGetValue(raw.Signature, out var until) && relative < until)
                    {
                        continue;
                    }

                    var result = await ExtractAsync(
                        input, seekable, origin, totalLength, window, len, raw, relative, baseOffset, outputDirectory, budget, ct).ConfigureAwait(false);
                    if (result is not { } extracted)
                    {
                        continue;
                    }
                    if (extracted.ExtentKnown)
                    {
                        carvedUntil[raw.Signature] = relative + extracted.Hit.Length;
                    }
                    yield return extracted.Hit;
                }

                progress?.Report(windowStart + ownedEnd);

                if (isLast)
                {
                    yield break;
                }

                Array.Copy(window, len - _overlap, window, 0, _overlap);
                windowStart += len - _overlap;
                retained = _overlap;
            }
        }
        finally
        {
            pool.Return(window);
        }
    }

    /// <summary>
    /// Finds every header in <c>window[0..length)</c> whose object starts before
    /// <paramref name="ownedEnd"/>, in ascending offset order (ties in rule order).
    /// </summary>
    private List<RawHit> ScanWindow(byte[] window, int length, int ownedEnd)
    {
        var bag = new ConcurrentBag<RawHit>();

        Parallel.For(0, _signatures.Count, index =>
        {
            var sig = _signatures[index];
            var span = window.AsSpan(0, length);
            var from = 0;
            while (from < length)
            {
                var idx = span[from..].IndexOf(sig.Header.AsSpan());
                if (idx < 0)
                {
                    break;
                }
                var at = from + idx;
                var objectStart = at - sig.HeaderOffset;
                if (objectStart >= ownedEnd)
                {
                    break;
                }
                // A negative start means the object began before this window: in the first
                // window that is not an object at all, later it belongs to the previous one.
                if (objectStart >= 0)
                {
                    bag.Add(new RawHit(sig, objectStart, index));
                }
                from = at + 1;
            }
        });

        var hits = bag.ToList();
        hits.Sort(static (a, b) =>
        {
            var byOffset = a.ObjectStart.CompareTo(b.ObjectStart);
            return byOffset != 0 ? byOffset : a.Order.CompareTo(b.Order);
        });
        return hits;
    }

    /// <summary>
    /// Decides what one header hit is, how long it is, and — when it is worth keeping and an
    /// output directory was given — writes it. Returns null for a hit that is not an object.
    /// </summary>
    private async Task<(CarveHit Hit, bool ExtentKnown)?> ExtractAsync(
        Stream input,
        bool seekable,
        long origin,
        long? totalLength,
        byte[] window,
        int windowLength,
        RawHit raw,
        long relative,
        long baseOffset,
        string? outputDirectory,
        ScanBudget budget,
        CancellationToken ct)
    {
        var sig = raw.Signature;
        var inWindow = windowLength - raw.ObjectStart;

        // A non-seekable source can only give what the window holds.
        var available = totalLength is { } total ? total - relative : inWindow;
        if (available <= 0)
        {
            return null;
        }
        inWindow = (int)Math.Min(inWindow, available);
        var limit = Math.Min(sig.MaxLengthBytes, available);
        var absolute = baseOffset + relative;

        CarveHit NotWritten(long length, string note) => new(sig.Label, sig.Extension, absolute, length, false, null, note);

        var savedPosition = seekable ? input.Position : 0;
        try
        {
            var source = new CarveSource(window, raw.ObjectStart, inWindow, seekable ? input : null, origin + relative, available, limit)
            {
                Budget = budget,
            };
            const string OutOfBudget = "not examined: this part of the source is dense with headers that lead nowhere, and the scan allowance for it is spent";

            long length;
            string? note = null;
            var extentKnown = false;

            long probed;
            try
            {
                probed = sig.Probe is null ? CarveProbeResult.UnknownLength : sig.Probe(source);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                // A probe that trips over the bytes it was given has not recognised anything.
                // The carve goes on; the hit is reported so the failure is visible.
                return (NotWritten(0, "the rule's structure check failed on these bytes: " + ex.GetType().Name), false);
            }
            if (probed == CarveProbeResult.NotThisFormat)
            {
                return null;
            }
            if (source.BudgetExhausted)
            {
                return (NotWritten(0, OutOfBudget), false);
            }
            if (probed < 0)
            {
                return (NotWritten(0, "header recognised, but the structure after it is damaged or incomplete"), false);
            }

            if (probed > 0)
            {
                if (probed > sig.MaxLengthBytes)
                {
                    return (NotWritten(0, string.Create(CultureInfo.InvariantCulture,
                        $"header declares {probed:N0} bytes, over the {sig.MaxLengthBytes:N0}-byte limit for this type")), false);
                }
                if (probed > available)
                {
                    return (NotWritten(available, string.Create(CultureInfo.InvariantCulture,
                        $"truncated: header declares {probed:N0} bytes, the source ends after {available:N0}")), false);
                }
                length = probed;
                extentKnown = true;
                note = "length from the format header";
            }
            else
            {
                var bodyStart = sig.HeaderOffset + sig.Header.Length;
                long footerAt = -1;
                if (sig.Footer is { Length: > 0 } footer)
                {
                    footerAt = source.IndexOf(footer, bodyStart, limit, ct);
                }

                if (footerAt >= 0)
                {
                    length = Math.Min(footerAt + sig.Footer!.Length + sig.FooterTrailerBytes, available);
                    extentKnown = true;
                }
                else if (source.BudgetExhausted)
                {
                    return (NotWritten(0, OutOfBudget), false);
                }
                else if (sig.Footer is { Length: > 0 } && sig.FooterRequired)
                {
                    return (NotWritten(0, string.Create(CultureInfo.InvariantCulture,
                        $"no end marker within {limit:N0} bytes")), false);
                }
                else
                {
                    // No length and no trailer: the object runs to the next header of its own
                    // kind, or to the unsized ceiling.
                    var cap = Math.Min(limit, UnsizedCarveLimitBytes);
                    var next = source.IndexOf(sig.Header, sig.HeaderOffset + 1, cap + bodyStart, ct);
                    if (next < 0 && source.BudgetExhausted)
                    {
                        // Without the search there is no telling where this object ends, and
                        // writing the ceiling for every such header is the flood being avoided.
                        return (NotWritten(0, OutOfBudget), false);
                    }
                    if (next >= 0 && next - sig.HeaderOffset < cap)
                    {
                        length = next - sig.HeaderOffset;
                        note = "length not recorded in this format: carved up to the next header of the same type";
                    }
                    else
                    {
                        length = cap;
                        if (cap < sig.MaxLengthBytes && cap < available)
                        {
                            note = string.Create(CultureInfo.InvariantCulture,
                                $"length not recorded in this format: carved the first {cap:N0} bytes");
                        }
                    }
                }
            }

            if (length <= 0)
            {
                return null;
            }

            var validated = true;
            string? outPath = null;

            if (sig.Validator is not null)
            {
                if (length > MaxValidatorBlobBytes)
                {
                    return (NotWritten(length, string.Create(CultureInfo.InvariantCulture,
                        $"too large to validate in memory ({length:N0} bytes)")), false);
                }

                var blob = new byte[length];
                source.Budget = null;       // reading the object itself is not speculative work
                var got = source.Read(0, blob);
                if (got <= 0)
                {
                    return null;
                }
                var memory = new ReadOnlyMemory<byte>(blob, 0, got);
                length = got;
                validated = sig.Validator(memory);
                if (validated && outputDirectory is not null)
                {
                    outPath = OutputPathFor(outputDirectory, absolute, sig.Extension);
                    await WriteBlobAsync(outPath, memory, ct).ConfigureAwait(false);
                }
            }
            else if (outputDirectory is not null)
            {
                outPath = OutputPathFor(outputDirectory, absolute, sig.Extension);
                var written = await CopyExtentAsync(source, outPath, length, ct).ConfigureAwait(false);
                if (written <= 0)
                {
                    TryDelete(outPath);
                    return null;
                }
                if (written < length)
                {
                    note = string.Create(CultureInfo.InvariantCulture,
                        $"the source ended after {written:N0} of {length:N0} bytes");
                    length = written;
                    extentKnown = false;
                }
            }

            return (new CarveHit(sig.Label, sig.Extension, absolute, length, validated, outPath, note), extentKnown && validated);
        }
        finally
        {
            if (seekable)
            {
                input.Position = savedPosition;
            }
        }
    }

    private static string OutputPathFor(string directory, long absoluteOffset, string extension)
        => Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{absoluteOffset:X12}.{extension}"));

    /// <summary>
    /// Streams <paramref name="length"/> bytes of the object to <paramref name="path"/>: the
    /// part already in the window first, the rest read from the source through one pooled
    /// buffer. Returns the bytes written, which is less than asked only if the source ends.
    /// A file left half-written by a failure or cancellation is removed.
    /// </summary>
    private static async Task<long> CopyExtentAsync(CarveSource source, string path, long length, CancellationToken ct)
    {
        long written = 0;
        var completed = false;
        try
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (fs.ConfigureAwait(false))
            {
                var fromWindow = (int)Math.Min(length, source.InWindow);
                if (fromWindow > 0)
                {
                    await fs.WriteAsync(source.WindowBytes[..fromWindow], ct).ConfigureAwait(false);
                    written = fromWindow;
                }

                if (written < length && source.SeekableStream is { } stream)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                    try
                    {
                        stream.Position = source.StreamBase + written;
                        while (written < length)
                        {
                            var want = (int)Math.Min(buffer.Length, length - written);
                            var n = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                            if (n <= 0)
                            {
                                break;
                            }
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            written += n;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            completed = true;
            return written;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(path);
            }
        }
    }

    private static async Task WriteBlobAsync(string path, ReadOnlyMemory<byte> blob, CancellationToken ct)
    {
        var completed = false;
        try
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (fs.ConfigureAwait(false))
            {
                await fs.WriteAsync(blob, ct).ConfigureAwait(false);
            }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Reads until <paramref name="destination"/> is full or the stream ends, returning the byte
    /// count. Streams may legitimately return short reads; treating one as end-of-input would
    /// mis-size the window and shift every subsequent offset.
    /// </summary>
    private static async Task<int> ReadFullAsync(Stream input, Memory<byte> destination, CancellationToken ct)
    {
        var filled = 0;
        while (filled < destination.Length)
        {
            var n = await input.ReadAsync(destination[filled..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }
        return filled;
    }

    private sealed record RawHit(CarveSignature Signature, int ObjectStart, int Order);
}
