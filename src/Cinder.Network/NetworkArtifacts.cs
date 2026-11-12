using Cinder.Artifacts;

namespace Cinder.Network;

public sealed record TcpFlow(
    string Source, int SourcePort,
    string Destination, int DestinationPort,
    DateTimeOffset First, DateTimeOffset Last,
    long PacketsIn, long PacketsOut, long BytesIn, long BytesOut,
    string? Ja3, string? Ja4, string? ServerName) : ArtifactBase(
        Source: "pcap.tcp",
        User: null,
        Timestamp: First,
        Summary: $"{Source}:{SourcePort} → {Destination}:{DestinationPort} ({BytesOut}↑/{BytesIn}↓)")
{
    /// <summary>
    /// The originating host. The constructor parameter is called <c>Source</c>, but a record
    /// does not synthesise a property for a parameter whose name an inherited member already
    /// has: <see cref="ArtifactBase.Source"/> is the artifact kind (<c>pcap.tcp</c>), so without
    /// this the address the flow came from was not readable from the record at all.
    /// </summary>
    public string SourceAddress { get; } = Source;
}

public sealed record HttpRequest(
    DateTimeOffset? Timestamp, string Method, string Url, int Status, long? ResponseBytes,
    string? Host, string? UserAgent) : ArtifactBase(
        Source: "pcap.http",
        User: null,
        Timestamp: Timestamp,
        Summary: $"{Method} {Url} → {Status}");

public sealed record DnsQuery(
    DateTimeOffset? Timestamp, string Client, string Server, string Name, string Type, string? Answer)
    : ArtifactBase(
        Source: "pcap.dns",
        User: null,
        Timestamp: Timestamp,
        Summary: $"{Name} ({Type}) → {Answer ?? "?"}");

public sealed record NetflowRecord(
    DateTimeOffset? Timestamp, string Source, string Destination, int Protocol,
    int SourcePort, int DestinationPort, long Packets, long Bytes)
    : ArtifactBase(
        Source: "netflow",
        User: null,
        Timestamp: Timestamp,
        Summary: $"{Source}:{SourcePort} → {Destination}:{DestinationPort} ({Bytes} B)")
{
    /// <summary>The originating host; see <see cref="TcpFlow.SourceAddress"/> for why this exists.</summary>
    public string SourceAddress { get; } = Source;
}
