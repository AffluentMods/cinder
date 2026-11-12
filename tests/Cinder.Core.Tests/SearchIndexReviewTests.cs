using Cinder.Search;
using FluentAssertions;
using Xunit;

namespace Cinder.Core.Tests;

/// <summary>
/// Behaviour the Search tool depends on: an empty folder is not an index, the same file indexed
/// twice is one result, a second build in one session works, and a query that cannot be parsed
/// is an error rather than "no hits".
/// </summary>
public sealed class SearchIndexReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-index-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Lucene can hold a handle briefly after Dispose on Windows; a leftover temp folder is harmless.
        }
    }

    private static IndexableDoc Doc(string id, string text) =>
        new(Id: id, Source: "file", User: null, Path: id, Summary: System.IO.Path.GetFileName(id), Text: text, Timestamp: null);

    [Fact]
    public void An_empty_or_missing_folder_is_not_an_index()
    {
        CaseIndex.Exists(_dir).Should().BeFalse();
        Directory.CreateDirectory(_dir);
        CaseIndex.Exists(_dir).Should().BeFalse();

        using var idx = new CaseIndex(_dir);
        idx.DocumentCount.Should().Be(0);
        idx.Search("anything").Should().BeEmpty("an index nothing was written to has nothing to find, and must not throw");
    }

    [Fact]
    public void Indexing_the_same_document_twice_updates_it_instead_of_duplicating_it()
    {
        using var idx = new CaseIndex(_dir);
        idx.OpenForWrite();
        idx.IndexDocument(Doc(@"C:\evidence\note.txt", "meet at the harbour"));
        idx.Commit();
        idx.IndexDocument(Doc(@"C:\evidence\note.txt", "meet at the harbour at nine"));
        idx.Commit();

        idx.DocumentCount.Should().Be(1);
        idx.Search("harbour").Should().HaveCount(1);
        idx.Search("nine").Should().HaveCount(1, "the second version replaced the first");
        CaseIndex.Exists(_dir).Should().BeTrue();
    }

    [Fact]
    public void Opening_for_write_twice_in_one_session_does_not_fail_on_the_write_lock()
    {
        using var idx = new CaseIndex(_dir);
        idx.OpenForWrite();
        idx.IndexDocument(Doc("a", "first folder"));
        idx.Commit();

        var second = () =>
        {
            idx.OpenForWrite();
            idx.IndexDocument(Doc("b", "second folder"));
            idx.Commit();
        };
        second.Should().NotThrow();
        idx.DocumentCount.Should().Be(2);
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("[draft")]
    [InlineData("text:")]
    public void A_query_that_cannot_be_parsed_is_an_error_not_zero_hits(string query)
    {
        using var idx = new CaseIndex(_dir);
        idx.OpenForWrite();
        idx.IndexDocument(Doc("a", "draft unterminated text"));
        idx.Commit();

        var search = () => idx.Search(query);
        search.Should().Throw<FormatException>();
    }
}
