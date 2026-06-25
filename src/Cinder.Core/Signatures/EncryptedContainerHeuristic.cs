namespace Cinder.Core.Signatures;

/// <summary>
/// Heuristic detector for TrueCrypt / VeraCrypt encrypted containers.
///
/// These formats deliberately have NO fixed magic header — the design goal is plausible
/// deniability, so the bytes on disk are indistinguishable from random data. We can't
/// "detect" them in the magic-number sense. But we can flag the SHAPE of a probable
/// container so an examiner sees a hint instead of nothing:
///
///   1. File size is a multiple of 512 (sector-aligned, as TC requires).
///   2. File size is at least 19 KB (smallest TC viable container size; real ones are MB+).
///   3. A header-sample of the first N bytes has very high Shannon entropy (~7.95+).
///   4. None of the standard <see cref="MagicSignatures"/> match the header.
///
/// All four conditions together produce a "probable encrypted container" badge. Each
/// alone is meaningless — random data files exist (`/dev/random` dumps, packed archives,
/// already-encrypted files of other types). The combination is what's diagnostic.
/// </summary>
public static class EncryptedContainerHeuristic
{
    public const double EntropyThreshold = 7.95;
    public const long MinContainerSize = 19 * 1024;

    public sealed record Result(bool Looks, double Entropy, string Reason);

    public static Result Inspect(
        ReadOnlySpan<byte> header,
        long fileLengthBytes,
        IReadOnlyList<SignatureMatch> existingMatches)
    {
        if (existingMatches.Count > 0)
        {
            var first = existingMatches[0].Signature.Label;
            return new Result(false, 0d, $"Has known signature: {first}");
        }
        if (fileLengthBytes < MinContainerSize)
        {
            return new Result(false, 0d, $"Below {MinContainerSize:N0}-byte minimum");
        }
        if (fileLengthBytes % 512 != 0)
        {
            return new Result(false, 0d, "Not sector-aligned (size % 512 != 0)");
        }
        if (header.Length < 1024)
        {
            return new Result(false, 0d, "Header sample too small for entropy");
        }
        var entropy = ShannonEntropy(header);
        if (entropy < EntropyThreshold)
        {
            return new Result(false, entropy, $"Entropy {entropy:F2} below {EntropyThreshold:F2} threshold");
        }
        return new Result(true, entropy,
            $"High entropy ({entropy:F2}), sector-aligned ({fileLengthBytes:N0} bytes), no known signature — probable TrueCrypt / VeraCrypt container");
    }

    private static double ShannonEntropy(ReadOnlySpan<byte> data)
    {
        Span<int> counts = stackalloc int[256];
        for (int i = 0; i < data.Length; i++)
        {
            counts[data[i]]++;
        }
        double e = 0d;
        double total = data.Length;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = counts[i] / total;
            e -= p * Math.Log2(p);
        }
        return e;
    }
}
