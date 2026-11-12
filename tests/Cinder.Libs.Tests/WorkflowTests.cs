using Cinder.Workflow;
using WorkflowDefinition = Cinder.Workflow.Workflow;
using FluentAssertions;
using Xunit;

namespace Cinder.Libs.Tests;

/// <summary>
/// A workflow is a repeatable acquisition plan loaded from a JSON file. A definition that is
/// wrong — a dependency cycle, an edge to a step that does not exist — used to run anyway with
/// the affected steps silently left out, and report "Done".
/// </summary>
public sealed class WorkflowTests
{
    private static WorkflowNode Node(string id, string kind = "step") => new() { Id = id, Kind = kind };
    private static WorkflowEdge Edge(string from, string to) => new() { From = from, To = to };

    private static (WorkflowRunner Runner, List<string> Ran) RecordingRunner()
    {
        var ran = new List<string>();
        var runner = new WorkflowRunner().Register("step", (node, _, _) =>
        {
            ran.Add(node.Id);
            return Task.FromResult<object?>(node.Id + "-done");
        });
        return (runner, ran);
    }

    [Fact]
    public void Nodes_come_out_after_everything_they_depend_on()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "acquire",
            Nodes = [Node("report"), Node("hash"), Node("open"), Node("verify"), Node("index")],
            Edges = [Edge("open", "hash"), Edge("hash", "verify"), Edge("open", "index"), Edge("verify", "report"), Edge("index", "report")],
        };

        var order = workflow.TopologicalOrder().Select(n => n.Id).ToList();

        order.Should().HaveCount(5);
        order.IndexOf("open").Should().BeLessThan(order.IndexOf("hash"));
        order.IndexOf("hash").Should().BeLessThan(order.IndexOf("verify"));
        order.IndexOf("verify").Should().BeLessThan(order.IndexOf("report"));
        order.IndexOf("index").Should().BeLessThan(order.IndexOf("report"));
    }

    [Fact]
    public async Task A_dependency_cycle_is_an_error_and_no_step_runs()
    {
        // open -> hash -> verify -> hash : hash and verify can never become ready. Kahn's
        // algorithm just never emits them — the run used to "complete" having done only "open".
        var workflow = new WorkflowDefinition
        {
            Name = "cyclic",
            Nodes = [Node("open"), Node("hash"), Node("verify"), Node("report")],
            Edges = [Edge("open", "hash"), Edge("hash", "verify"), Edge("verify", "hash"), Edge("verify", "report")],
        };
        var (runner, ran) = RecordingRunner();

        var order = () => workflow.TopologicalOrder();
        var run = async () => await runner.RunAsync(workflow);

        order.Should().Throw<WorkflowDefinitionException>()
            .WithMessage("*cycle*").WithMessage("*'hash'*").WithMessage("*'verify'*").WithMessage("*'report'*");
        await run.Should().ThrowAsync<WorkflowDefinitionException>();
        ran.Should().BeEmpty("a plan that cannot be carried out in full must not be started");
    }

    [Fact]
    public void A_node_that_depends_on_itself_is_a_cycle()
    {
        var workflow = new WorkflowDefinition { Name = "self", Nodes = [Node("a")], Edges = [Edge("a", "a")] };
        var act = () => workflow.TopologicalOrder();
        act.Should().Throw<WorkflowDefinitionException>().WithMessage("*'a'*");
    }

    [Theory]
    [InlineData("open", "hsah", "to unknown node 'hsah'")]
    [InlineData("oepn", "hash", "from unknown node 'oepn'")]
    public async Task An_edge_naming_a_node_that_does_not_exist_is_an_error_that_names_it(string from, string to, string expected)
    {
        var workflow = new WorkflowDefinition { Name = "typo", Nodes = [Node("open"), Node("hash")], Edges = [Edge(from, to)] };
        var (runner, ran) = RecordingRunner();

        var order = () => workflow.TopologicalOrder();
        var run = async () => await runner.RunAsync(workflow);

        order.Should().Throw<WorkflowDefinitionException>().WithMessage($"*{expected}*");
        await run.Should().ThrowAsync<WorkflowDefinitionException>();
        ran.Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_and_empty_node_ids_are_errors()
    {
        var duplicate = new WorkflowDefinition { Name = "dup", Nodes = [Node("a"), Node("b"), Node("a")] };
        var empty = new WorkflowDefinition { Name = "empty", Nodes = [Node("")] };

        ((Action)(() => duplicate.TopologicalOrder())).Should().Throw<WorkflowDefinitionException>().WithMessage("*'a'*more than once*");
        ((Action)(() => empty.TopologicalOrder())).Should().Throw<WorkflowDefinitionException>().WithMessage("*no id*");
    }

    [Fact]
    public void A_definition_error_is_an_invalid_operation_so_existing_handlers_catch_it()
    {
        typeof(WorkflowDefinitionException).Should().BeDerivedFrom<InvalidOperationException>();
    }

    [Fact]
    public void A_cyclic_workflow_loaded_from_json_is_rejected()
    {
        const string Json = """
            { "Name": "from-file",
              "Nodes": [ { "Id": "a", "Kind": "step" }, { "Id": "b", "Kind": "step" } ],
              "Edges": [ { "From": "a", "To": "b" }, { "From": "b", "To": "a" } ] }
            """;

        var act = () => WorkflowDefinition.FromJson(Json).TopologicalOrder();

        act.Should().Throw<WorkflowDefinitionException>();
    }

    [Fact]
    public void Null_lists_in_the_json_are_a_definition_error_not_a_null_reference()
    {
        var act = () => WorkflowDefinition.FromJson("""{ "Name": "nulls", "Nodes": null, "Edges": null }""").TopologicalOrder();
        act.Should().Throw<WorkflowDefinitionException>();
    }

    [Fact]
    public async Task A_valid_workflow_runs_every_step_once_in_order()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "ok",
            Nodes = [Node("c"), Node("b"), Node("a")],
            Edges = [Edge("a", "b"), Edge("b", "c")],
        };
        var (runner, ran) = RecordingRunner();

        var outputs = await runner.RunAsync(workflow);

        ran.Should().Equal("a", "b", "c");
        outputs.Should().ContainKeys("a", "b", "c");
        outputs["c"].Should().Be("c-done");
    }
}
