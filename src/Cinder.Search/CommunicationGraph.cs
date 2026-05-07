namespace Cinder.Search;

/// <summary>Force-directed comm graph: who-talked-to-whom across email + chat artifacts.</summary>
public sealed class CommunicationGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GraphEdge> _edges = new();

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;
    public IReadOnlyList<GraphEdge> Edges => _edges;

    public void AddInteraction(string from, string to, string source, DateTimeOffset? timestamp, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return;
        }
        var a = _nodes.TryGetValue(from, out var na) ? na : (_nodes[from] = new GraphNode(from));
        var b = _nodes.TryGetValue(to, out var nb) ? nb : (_nodes[to] = new GraphNode(to));
        a.OutCount++;
        b.InCount++;
        _edges.Add(new GraphEdge(from, to, source, timestamp, subject));
    }
}

public sealed class GraphNode
{
    public string Identity { get; }
    public int InCount { get; set; }
    public int OutCount { get; set; }
    public GraphNode(string identity) => Identity = identity;
}

public sealed record GraphEdge(string From, string To, string Source, DateTimeOffset? Timestamp, string? Subject);
