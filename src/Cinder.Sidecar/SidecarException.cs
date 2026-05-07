namespace Cinder.Sidecar;

/// <summary>Raised when a sidecar returns an error or its stream becomes invalid.</summary>
public sealed class SidecarException : Exception
{
    public int? Code { get; }

    public SidecarException(string message) : base(message) { }
    public SidecarException(string message, Exception inner) : base(message, inner) { }
    public SidecarException(int code, string message) : base(message) => Code = code;
}
