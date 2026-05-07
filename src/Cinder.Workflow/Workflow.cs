using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cinder.Workflow;

/// <summary>
/// Visual node-graph workflow. Nodes are typed steps (Open, Hash, Carve, Index, Report, ...);
/// edges express "after". Execution drives a topological walk; each step gets the prior
/// step's outputs in its inputs map.
///
/// Workflows serialize as JSON so the report builder's "playbook" export (Phase 8) can ship a
/// repeatable acquisition plan with the report.
/// </summary>
public sealed class Workflow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public List<WorkflowNode> Nodes { get; init; } = new();
    public List<WorkflowEdge> Edges { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static Workflow FromJson(string json) => JsonSerializer.Deserialize<Workflow>(json) ?? new Workflow();

    public IEnumerable<WorkflowNode> TopologicalOrder()
    {
        var indegree = Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var e in Edges)
        {
            indegree[e.To]++;
        }
        var ready = new Queue<WorkflowNode>(Nodes.Where(n => indegree[n.Id] == 0));
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            yield return node;
            foreach (var edge in Edges.Where(e => e.From == node.Id))
            {
                if (--indegree[edge.To] == 0)
                {
                    ready.Enqueue(Nodes.First(n => n.Id == edge.To));
                }
            }
        }
    }
}

public sealed class WorkflowNode
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";   // "open-image", "hash", "image-verify", "carve", "fs-enumerate", "registry", "index", "report", "ai-summary"
    public Dictionary<string, JsonElement> Parameters { get; init; } = new();
}

public sealed class WorkflowEdge
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
}

public sealed class WorkflowRunner
{
    private readonly Dictionary<string, Func<WorkflowNode, IDictionary<string, object?>, CancellationToken, Task<object?>>> _kinds = new(StringComparer.Ordinal);

    public WorkflowRunner Register(string kind, Func<WorkflowNode, IDictionary<string, object?>, CancellationToken, Task<object?>> handler)
    {
        _kinds[kind] = handler;
        return this;
    }

    public async Task<IReadOnlyDictionary<string, object?>> RunAsync(Workflow workflow, CancellationToken ct = default)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var node in workflow.TopologicalOrder())
        {
            ct.ThrowIfCancellationRequested();
            if (!_kinds.TryGetValue(node.Kind, out var handler))
            {
                throw new InvalidOperationException($"Unknown workflow node kind: {node.Kind}");
            }
            var result = await handler(node, outputs, ct).ConfigureAwait(false);
            outputs[node.Id] = result;
        }
        return outputs;
    }
}
