namespace Cinder.Hex;

/// <summary>A user-placed marker at a byte offset, with optional label + color.</summary>
public sealed record Bookmark(long Offset, string Label, string Color = "#FF7A1A");
