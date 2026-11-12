namespace Cinder.Imaging;

/// <summary>
/// Implemented by the streams <see cref="EvidenceOpener"/> and the container readers hand out
/// when the media they expose is decoded from a container that can be damaged. A chunk that
/// cannot be decoded is zero-filled so offsets stay true, which means the bytes alone do not
/// say whether they are evidence or filler — this is where a consumer finds out.
///
/// <para>The list grows as the stream is read, so it is only complete once the caller has read
/// everything it is going to read. Anything that reports on a whole image (a hash, a
/// conversion, a carve) should check it afterwards and say so when it is not empty.</para>
/// </summary>
public interface IEvidenceDamageSource
{
    /// <summary>Indexes of the chunks that were zero-filled so far, in the order first read.</summary>
    IReadOnlyList<int> DamagedChunks { get; }
}
