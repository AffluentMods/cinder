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

    /// <summary>
    /// The nodes in an order where every node comes after everything it depends on.
    ///
    /// <para>The graph is validated first, and eagerly — this is not a lazy iterator, so a bad
    /// definition fails here rather than part-way through a run. A workflow is a repeatable
    /// acquisition plan: one that quietly leaves steps out is worse than one that refuses to
    /// start. Kahn's algorithm simply never reaches the nodes of a cycle, which used to mean a
    /// cyclic workflow "completed" having skipped them.</para>
    /// </summary>
    /// <exception cref="WorkflowDefinitionException">
    /// A node id is empty or duplicated, an edge names a node that does not exist, or the
    /// edges form a cycle. The message names the offending ids.
    /// </exception>
    public IReadOnlyList<WorkflowNode> TopologicalOrder()
    {
        // "Nodes": null in the JSON overwrites the initialiser.
        if (Nodes is null || Edges is null || Nodes.Any(n => n is null) || Edges.Any(e => e is null))
        {
            throw new WorkflowDefinitionException($"Workflow '{Name}' has a null node or edge list, or a null entry in one.");
        }

        var byId = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                throw new WorkflowDefinitionException($"Workflow '{Name}' has a node with no id (kind '{node.Kind}').");
            }
            if (!byId.TryAdd(node.Id, node))
            {
                throw new WorkflowDefinitionException($"Workflow '{Name}' defines node id '{node.Id}' more than once.");
            }
        }

        var indegree = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            if (edge.From is null || edge.To is null)
            {
                throw new WorkflowDefinitionException($"Workflow '{Name}' has an edge with a null endpoint.");
            }
            if (!byId.ContainsKey(edge.From))
            {
                throw new WorkflowDefinitionException(
                    $"Workflow '{Name}' has an edge from unknown node '{edge.From}' (to '{edge.To}').");
            }
            if (!byId.ContainsKey(edge.To))
            {
                throw new WorkflowDefinitionException(
                    $"Workflow '{Name}' has an edge to unknown node '{edge.To}' (from '{edge.From}').");
            }
            outgoing[edge.From].Add(edge.To);
            indegree[edge.To]++;
        }

        var ordered = new List<WorkflowNode>(Nodes.Count);
        var ready = new Queue<WorkflowNode>(Nodes.Where(n => indegree[n.Id] == 0));
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            ordered.Add(node);
            foreach (var next in outgoing[node.Id])
            {
                if (--indegree[next] == 0)
                {
                    ready.Enqueue(byId[next]);
                }
            }
        }

        if (ordered.Count != Nodes.Count)
        {
            var stuck = Nodes.Where(n => indegree[n.Id] > 0).Select(n => $"'{n.Id}'");
            throw new WorkflowDefinitionException(
                $"Workflow '{Name}' has a dependency cycle; these nodes can never run: {string.Join(", ", stuck)}.");
        }
        return ordered;
    }
}

/// <summary>The workflow definition itself is invalid — nothing was run.</summary>
public sealed class WorkflowDefinitionException(string message) : InvalidOperationException(message);

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
