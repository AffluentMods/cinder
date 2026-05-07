namespace Cinder.Core.Signatures;

/// <summary>
/// A file-format signature: a sequence of bytes (with optional don't-care positions) at a given
/// offset, plus a human label, canonical extension, and MIME type. Phase 1 uses these for the
/// "extension vs. content" mismatch flag in the hex viewer.
/// </summary>
public sealed record MagicSignature(
    string Label,
    string Extension,
    string Mime,
    long Offset,
    byte?[] Pattern)
{
    public bool Matches(ReadOnlySpan<byte> buffer)
    {
        if (Offset < 0 || Offset >= buffer.Length)
        {
            return false;
        }
        if (buffer.Length - Offset < Pattern.Length)
        {
            return false;
        }
        for (int i = 0; i < Pattern.Length; i++)
        {
            var expected = Pattern[i];
            if (expected is null)
            {
                continue;
            }
            if (buffer[(int)(Offset + i)] != expected.Value)
            {
                return false;
            }
        }
        return true;
    }
}
