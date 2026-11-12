namespace Cinder.Carving;

/// <summary>
/// A carving rule. At its simplest: a header pattern, an optional footer and a maximum length.
/// The optional members let a rule do what a bare header/footer pair cannot — reject a
/// coincidental header before anything is read, and take the object's real length from the
/// format's own header instead of carving a fixed maximum.
/// </summary>
/// <param name="Label">Human name of the format.</param>
/// <param name="Extension">Extension given to carved files, without a dot.</param>
/// <param name="Header">Bytes that mark the format. Must not be empty.</param>
/// <param name="Footer">Bytes that end the object, or null when the format has no trailer.</param>
/// <param name="MaxLengthBytes">Upper bound on a carved object of this type.</param>
/// <param name="Validator">
/// Optional whole-object check, run on the extracted bytes before they are written. Because it
/// needs the object in memory it is only applied to objects up to
/// <see cref="FileCarver.MaxValidatorBlobBytes"/>; a larger object is reported unvalidated and
/// not written. Prefer <see cref="Probe"/>, which works on any size.
/// </param>
public sealed record CarveSignature(
    string Label,
    string Extension,
    byte[] Header,
    byte[]? Footer,
    long MaxLengthBytes,
    Func<ReadOnlyMemory<byte>, bool>? Validator = null)
{
    /// <summary>
    /// How far into the object <see cref="Header"/> sits. Zero for most formats; 4 for
    /// ISO-BMFF, whose <c>ftyp</c> tag follows a 4-byte box size; 8 for the form type of a
    /// RIFF file. The carved object starts this many bytes before the matched pattern.
    /// </summary>
    public int HeaderOffset { get; init; }

    /// <summary>
    /// Structural check and length source, run against the bytes at the hit before anything
    /// is extracted. See <see cref="CarveProbe"/> for the result convention.
    /// </summary>
    public CarveProbe? Probe { get; init; }

    /// <summary>
    /// When true and <see cref="Footer"/> is not found within <see cref="MaxLengthBytes"/>,
    /// the hit is reported unvalidated and nothing is written. When false (the default, and
    /// the historical behaviour for caller-defined rules) the object is carved up to the
    /// carver's unsized limit instead.
    /// </summary>
    public bool FooterRequired { get; init; }

    /// <summary>
    /// Bytes that belong to the object after the footer pattern — 18 for ZIP, whose
    /// end-of-central-directory record continues past its 4-byte signature.
    /// </summary>
    public int FooterTrailerBytes { get; init; }
}

/// <summary>
/// Inspects the bytes at a header hit. Returns the object's exact length in bytes when the
/// format declares one; <see cref="CarveProbeResult.UnknownLength"/> when the header is
/// plausible but carries no length; <see cref="CarveProbeResult.NotThisFormat"/> when the
/// header pattern matched by coincidence (the hit is dropped without being reported); or
/// <see cref="CarveProbeResult.Damaged"/> when the header is genuine but the structure after
/// it cannot be followed (the hit is reported unvalidated and nothing is written).
/// </summary>
public delegate long CarveProbe(CarveSource source);

/// <summary>Non-length results of a <see cref="CarveProbe"/>.</summary>
public static class CarveProbeResult
{
    public const long UnknownLength = 0;
    public const long NotThisFormat = -1;
    public const long Damaged = -2;
}
