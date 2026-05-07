namespace Cinder.Carving;

/// <summary>A carving rule: header pattern + optional footer + max length + validator.</summary>
public sealed record CarveSignature(
    string Label,
    string Extension,
    byte[] Header,
    byte[]? Footer,
    long MaxLengthBytes,
    Func<ReadOnlyMemory<byte>, bool>? Validator = null);
