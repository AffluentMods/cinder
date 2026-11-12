using System.Text.Json;
using Cinder.Cases;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Branch / commit / merge behaviour for the per-examiner case state. The failures these pin
/// were all silent: a merge that discarded the side the examiner chose, a commit attached to
/// no branch, a deleted bookmark that came back.
/// </summary>
public sealed class CaseBranchingTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"cinder-branching-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> payload, string key) => payload[key].GetString()!;

    [Fact]
    public void A_branch_can_be_created_in_a_new_case_before_main_has_any_commit()
    {
        var repo = new CaseBranching(_db);

        repo.CreateBranch("alice");

        repo.ListBranches().Should().BeEquivalentTo(["alice", "main"]);
        repo.GetHead("alice").Should().BeNull();
        repo.Commit("alice", "alice", "first note", new { note = "a" }).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Creating_a_branch_from_a_parent_that_does_not_exist_is_an_error()
    {
        var repo = new CaseBranching(_db);

        var act = () => repo.CreateBranch("alice", parent: "no-such-branch");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no-such-branch*");
        repo.ListBranches().Should().Equal("main");
    }

    [Fact]
    public void Creating_a_branch_twice_is_an_error_not_a_constraint_violation()
    {
        var repo = new CaseBranching(_db);
        repo.CreateBranch("alice");

        var act = () => repo.CreateBranch("alice");

        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public void A_commit_to_a_branch_that_does_not_exist_is_refused_and_nothing_is_stored()
    {
        var repo = new CaseBranching(_db);

        var act = () => repo.Commit("mian", "alice", "typo in the branch name", new { tag = "x" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*mian*");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM commits;";
        Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(0, "an orphaned commit is lost work that looks saved");
    }

    [Fact]
    public void A_commit_advances_the_branch_head_and_its_payload_can_be_read_back()
    {
        var repo = new CaseBranching(_db);

        var id = repo.Commit("main", "alice", "start", new { tag = "red", note = "n1" });

        repo.GetHead("main").Should().Be(id);
        Text(repo.GetPayload("main"), "tag").Should().Be("red");
    }

    [Fact]
    public void The_resolver_choosing_theirs_puts_their_value_in_the_merge()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice retags", new { tag = "blue" });
        repo.Commit("main", "lead", "lead retags", new { tag = "green" });

        var result = repo.Merge("alice", "main", "lead", _ => MergeResolution.Theirs);

        result.Ok.Should().BeTrue();
        result.Conflicts.Should().ContainSingle().Which.Key.Should().Be("tag");
        Text(repo.GetPayload("main"), "tag").Should().Be("blue", "the examiner chose 'theirs'");
    }

    [Fact]
    public void The_resolver_choosing_ours_keeps_our_value()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice retags", new { tag = "blue" });
        repo.Commit("main", "lead", "lead retags", new { tag = "green" });

        var result = repo.Merge("alice", "main", "lead", _ => MergeResolution.Ours);

        result.Ok.Should().BeTrue();
        Text(repo.GetPayload("main"), "tag").Should().Be("green");
    }

    [Fact]
    public void A_manual_resolution_aborts_with_every_conflict_and_commits_nothing()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { a = "1", b = "1" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice", new { a = "2", b = "2" });
        var headBefore = repo.Commit("main", "lead", "lead", new { a = "3", b = "3" });

        var result = repo.Merge("alice", "main", "lead", _ => MergeResolution.Manual);

        result.Ok.Should().BeFalse();
        result.NewHeadCommit.Should().BeNull();
        result.Conflicts.Select(c => c.Key).Should().Equal("a", "b");
        repo.GetHead("main").Should().Be(headBefore);
    }

    [Fact]
    public void A_change_made_on_only_one_side_merges_without_asking()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { tag = "red", note = "n" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice edits note", new { tag = "red", note = "alice's note" });
        repo.Commit("main", "lead", "lead edits tag", new { tag = "green", note = "n" });

        var asked = 0;
        var result = repo.Merge("alice", "main", "lead", _ => { asked++; return MergeResolution.Manual; });

        result.Ok.Should().BeTrue();
        asked.Should().Be(0, "each key was changed by one side only — that is not a conflict");
        var merged = repo.GetPayload("main");
        Text(merged, "tag").Should().Be("green");
        Text(merged, "note").Should().Be("alice's note");
    }

    [Fact]
    public void A_key_we_deleted_is_not_resurrected_from_a_branch_that_never_touched_it()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { bookmark = "keep?", tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice adds a note", new { bookmark = "keep?", tag = "red", note = "x" });
        repo.Commit("main", "lead", "lead removes the bookmark", new { tag = "red" });

        var result = repo.Merge("alice", "main", "lead", _ => MergeResolution.Manual);

        result.Ok.Should().BeTrue();
        var merged = repo.GetPayload("main");
        merged.Should().NotContainKey("bookmark", "a two-way merge brought it back from the other side");
        merged.Should().ContainKey("note");
    }

    [Fact]
    public void A_key_they_deleted_is_removed_when_we_did_not_change_it()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { bookmark = "b", tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice removes the bookmark", new { tag = "red" });

        var result = repo.Merge("alice", "main", "lead", _ => MergeResolution.Manual);

        result.Ok.Should().BeTrue();
        repo.GetPayload("main").Should().NotContainKey("bookmark");
    }

    [Fact]
    public void A_delete_against_an_edit_is_a_conflict_with_an_undefined_side()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { bookmark = "b" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice removes it", new { });
        repo.Commit("main", "lead", "lead edits it", new { bookmark = "b2" });

        MergeConflict? seen = null;
        var result = repo.Merge("alice", "main", "lead", c => { seen = c; return MergeResolution.Theirs; });

        result.Ok.Should().BeTrue();
        seen!.Theirs.ValueKind.Should().Be(JsonValueKind.Undefined);
        seen.Ours.GetString().Should().Be("b2");
        repo.GetPayload("main").Should().NotContainKey("bookmark", "'theirs' was a deletion");
    }

    [Fact]
    public void A_second_merge_uses_the_previous_merge_as_its_base()
    {
        // After alice is merged, main deliberately sets the tag back. Merging alice again must
        // not re-apply her old change: relative to the last merge she changed nothing.
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice retags", new { tag = "blue" });
        repo.Merge("alice", "main", "lead", _ => MergeResolution.Manual).Ok.Should().BeTrue();
        Text(repo.GetPayload("main"), "tag").Should().Be("blue");
        repo.Commit("main", "lead", "lead reverts the tag", new { tag = "red" });

        var asked = 0;
        var result = repo.Merge("alice", "main", "lead", _ => { asked++; return MergeResolution.Theirs; });

        result.Ok.Should().BeTrue();
        asked.Should().Be(0);
        Text(repo.GetPayload("main"), "tag").Should().Be("red");
    }

    [Fact]
    public void Merging_from_a_branch_that_does_not_exist_is_an_error()
    {
        var repo = new CaseBranching(_db);
        var head = repo.Commit("main", "lead", "base", new { tag = "red" });

        var act = () => repo.Merge("ghost", "main", "lead", _ => MergeResolution.Ours);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ghost*");
        repo.GetHead("main").Should().Be(head, "no 'Merge ghost into main' commit may appear");
    }

    [Fact]
    public void A_merge_is_abandoned_if_the_target_moved_while_conflicts_were_being_resolved()
    {
        var repo = new CaseBranching(_db);
        repo.Commit("main", "lead", "base", new { tag = "red" });
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice", new { tag = "blue" });
        repo.Commit("main", "lead", "lead", new { tag = "green" });

        string? concurrent = null;
        var act = () => repo.Merge("alice", "main", "lead", _ =>
        {
            // Another examiner commits to main while this one is looking at the conflict dialog.
            concurrent = new CaseBranching(_db).Commit("main", "bob", "bob", new { tag = "yellow" });
            return MergeResolution.Theirs;
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*changed while the merge*");
        repo.GetHead("main").Should().Be(concurrent);
        Text(repo.GetPayload("main"), "tag").Should().Be("yellow", "bob's commit must not be overwritten by a stale merge");
    }

    [Fact]
    public void A_payload_that_is_not_a_json_object_is_refused()
    {
        var repo = new CaseBranching(_db);
        var act = () => repo.Commit("main", "lead", "bad", new[] { 1, 2, 3 });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_case_file_from_before_merge_parents_were_recorded_is_upgraded_in_place()
    {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE branches (name TEXT PRIMARY KEY, parent TEXT NULL, head_commit TEXT NULL);
                CREATE TABLE commits (id TEXT PRIMARY KEY, parent_id TEXT NULL, branch TEXT NOT NULL, examiner TEXT NOT NULL,
                                      timestamp_utc TEXT NOT NULL, message TEXT NOT NULL, payload_json TEXT NOT NULL);
                INSERT INTO commits VALUES ('aaaaaaaaaaaaaaaa', NULL, 'main', 'lead', '2026-01-01T00:00:00Z', 'old', '{"tag":"red"}');
                INSERT INTO branches VALUES ('main', NULL, 'aaaaaaaaaaaaaaaa');
                """;
            cmd.ExecuteNonQuery();
        }

        var repo = new CaseBranching(_db);
        repo.CreateBranch("alice");
        repo.Commit("alice", "alice", "alice", new { tag = "red", note = "n" });

        repo.Merge("alice", "main", "lead", _ => MergeResolution.Manual).Ok.Should().BeTrue();
        repo.GetPayload("main").Should().ContainKey("note");
    }
}
