namespace Cinder.Hex;

/// <summary>
/// A coloured byte range overlaid on the hex view to surface structural meaning (e.g. "MFT
/// header", "NTFS BPB", "PE optional header"). Phase 1 ships the rendering; structure providers
/// are added per-format in later phases.
/// </summary>
public sealed record StructureOverlay(long Offset, long Length, string Label, string Color);
