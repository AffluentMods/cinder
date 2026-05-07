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
        Summary: $"{Source}:{SourcePort} → {Destination}:{DestinationPort} ({BytesOut}↑/{BytesIn}↓)");

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
        Summary: $"{Source}:{SourcePort} → {Destination}:{DestinationPort} ({Bytes} B)");
