using System.Collections.Concurrent;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Cinder.Search;

/// <summary>
/// Lucene.NET-backed full-text index for a single case. Documents are append-only; deletions
/// happen only when the case is rebuilt.
///
/// Index schema:
///   id        StringField   (stored, unique)            — synthetic GUID
///   source    StringField   (stored, indexed)           — "browser.history", "evtx", etc.
///   user      StringField   (stored, indexed)           — username, may be empty
///   path      TextField     (stored, indexed, analyzed)
///   text      TextField     (indexed, analyzed)         — main searchable body
///   summary   StringField   (stored)                    — display string in result list
///   timestamp NumericDocValuesField + StoredField       — Unix milliseconds
///   tags      TextField     (stored, indexed)           — space-separated user tags
/// </summary>
public sealed class CaseIndex : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    private readonly FSDirectory _dir;
    private readonly StandardAnalyzer _analyzer;
    private IndexWriter? _writer;
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;
    private readonly object _readerLock = new();

    public CaseIndex(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        _dir = FSDirectory.Open(path);
        _analyzer = new StandardAnalyzer(Version);
    }

    public void OpenForWrite()
    {
        var config = new IndexWriterConfig(Version, _analyzer)
        {
            OpenMode = OpenMode.CREATE_OR_APPEND,
            RAMBufferSizeMB = 64,
        };
        _writer = new IndexWriter(_dir, config);
    }

    public void IndexDocument(IndexableDoc doc)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("Call OpenForWrite() before indexing.");
        }
        var d = new Document
        {
            new StringField("id", doc.Id, Field.Store.YES),
            new StringField("source", doc.Source, Field.Store.YES),
            new StringField("user", doc.User ?? "", Field.Store.YES),
            new TextField("path", doc.Path ?? "", Field.Store.YES),
            new TextField("text", doc.Text ?? "", Field.Store.NO),
            new StringField("summary", doc.Summary ?? "", Field.Store.YES),
            new TextField("tags", string.Join(" ", doc.Tags ?? []), Field.Store.YES),
        };
        if (doc.Timestamp is { } ts)
        {
            var ms = ts.ToUnixTimeMilliseconds();
            d.Add(new Int64Field("timestamp", ms, Field.Store.YES));
            d.Add(new NumericDocValuesField("timestamp", ms));
        }
        _writer.AddDocument(d);
    }

    public void Commit() => _writer?.Commit();

    public IReadOnlyList<SearchHit> Search(string query, int max = 100)
    {
        EnsureReader();
        var parser = new MultiFieldQueryParser(Version,
            new[] { "text", "path", "summary", "tags", "user", "source" }, _analyzer);
        Query q;
        try { q = parser.Parse(query); }
        catch (ParseException) { return []; }
        var top = _searcher!.Search(q, max);
        var hits = new List<SearchHit>(top.ScoreDocs.Length);
        foreach (var sd in top.ScoreDocs)
        {
            var d = _searcher.Doc(sd.Doc);
            hits.Add(new SearchHit(
                Id: d.Get("id"),
                Source: d.Get("source"),
                User: d.Get("user"),
                Path: d.Get("path"),
                Summary: d.Get("summary"),
                Score: sd.Score,
                Timestamp: long.TryParse(d.Get("timestamp"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null));
        }
        return hits;
    }

    private void EnsureReader()
    {
        lock (_readerLock)
        {
            if (_reader is not null && _writer is not null)
            {
                var refreshed = DirectoryReader.OpenIfChanged(_reader, _writer, applyAllDeletes: true);
                if (refreshed is not null)
                {
                    _reader.Dispose();
                    _reader = refreshed;
                    _searcher = new IndexSearcher(_reader);
                }
                return;
            }
            _reader ??= _writer is null ? DirectoryReader.Open(_dir) : DirectoryReader.Open(_writer, applyAllDeletes: true);
            _searcher = new IndexSearcher(_reader);
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _analyzer.Dispose();
        _dir.Dispose();
    }
}

public sealed record IndexableDoc(
    string Id,
    string Source,
    string? User,
    string? Path,
    string? Summary,
    string? Text,
    DateTimeOffset? Timestamp,
    IReadOnlyList<string>? Tags = null);

public sealed record SearchHit(
    string Id,
    string Source,
    string? User,
    string? Path,
    string? Summary,
    double Score,
    DateTimeOffset? Timestamp);
