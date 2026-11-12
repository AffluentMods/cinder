using System.Text.Json.Nodes;
using Cinder.Filesystems;
using FluentAssertions;
using Xunit;

namespace Cinder.Sidecar.Tests;

/// <summary>
/// PytskFilesystemParser against canned replies shaped like <c>parsers/filesystem/fs_worker.py</c>'s
/// (pytsk3 itself is an optional native dependency and is not needed here).
/// </summary>
public sealed class PytskFilesystemParserTests
{
    private static string Page(int first, int count, long nextCursor, bool? done, int errors = 0)
    {
        var entries = new JsonArray();
        for (var i = first; i < first + count; i++)
        {
            entries.Add(new JsonObject
            {
                ["inode"] = 100 + i,
                ["path"] = $"/dir/file{i:0000}.txt",
                ["name"] = $"file{i:0000}.txt",
                ["size"] = i,
                ["is_dir"] = false,
                ["is_deleted"] = false,
                ["btime"] = "2023-11-14T22:13:20+00:00",
                ["mtime"] = "2023-11-14T22:15:00+00:00",
                ["atime"] = null,
                ["ctime"] = null,
                ["owner"] = "0",
                ["group"] = "0",
                ["mode"] = 420,
            });
        }
        var page = new JsonObject { ["entries"] = entries, ["next_cursor"] = nextCursor, ["errors"] = errors };
        if (done is { } d)
        {
            page["done"] = d;
        }
        return page.ToJsonString();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task Listing_follows_the_cursor_until_the_worker_says_done_on_one_channel()
    {
        var size = PytskFilesystemParser.ListingPageSize;
        var factory = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(
            Page(0, size, size, done: false),
            Page(size, size, 2 * size, done: false),
            Page(2 * size, 3, 2 * size + 3, done: true, errors: 2)));
        var parser = new PytskFilesystemParser(factory.Open);

        var pages = await Collect(parser.EnumeratePagesAsync("img.dd", offsetBytes: 1048576));

        pages.SelectMany(p => p.Entries).Select(e => e.Name)
            .Should().Equal(Enumerable.Range(0, 2 * size + 3).Select(i => $"file{i:0000}.txt"));
        factory.Opened.Should().HaveCount(1, "the worker keeps its walk between pages of one connection");
        var calls = factory.Calls.ToList();
        calls.Should().OnlyContain(c => c.Method == "enumerate_page");
        calls.Select(c => c.Params["cursor"]!.GetValue<long>()).Should().Equal(0, size, 2 * size);
        calls.Should().OnlyContain(c => c.Params["limit"]!.GetValue<int>() == size
                                        && c.Params["offset"]!.GetValue<long>() == 1048576
                                        && c.Params["image_path"]!.GetValue<string>() == "img.dd");
        pages[^1].Done.Should().BeTrue();
        pages[^1].Errors.Should().Be(2, "the count of unreadable entries is part of the listing");
        pages[0].Errors.Should().Be(0);
    }

    [Fact]
    public async Task A_full_page_marked_done_ends_the_listing_and_a_short_page_not_marked_done_does_not()
    {
        var size = PytskFilesystemParser.ListingPageSize;
        var fullAndDone = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(Page(0, size, size, done: true)));
        (await Collect(new PytskFilesystemParser(fullAndDone.Open).EnumerateAsync("img.dd"))).Should().HaveCount(size);
        fullAndDone.Calls.Should().HaveCount(1, "'done' is the end, whatever the page size");

        var shortNotDone = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(
            Page(0, 2, 2, done: false), Page(2, 1, 3, done: true)));
        (await Collect(new PytskFilesystemParser(shortNotDone.Open).EnumerateAsync("img.dd"))).Should().HaveCount(3);
        shortNotDone.Calls.Select(c => c.Params["cursor"]!.GetValue<long>()).Should().Equal(0, 2);
    }

    [Fact]
    public async Task A_worker_that_does_not_send_done_ends_on_a_short_page()
    {
        var size = PytskFilesystemParser.ListingPageSize;
        var factory = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(
            Page(0, size, size, done: null), Page(size, 10, size + 10, done: null)));

        var entries = await Collect(new PytskFilesystemParser(factory.Open).EnumerateAsync("img.dd"));

        entries.Should().HaveCount(size + 10);
    }

    [Fact]
    public async Task A_reply_that_would_end_or_loop_the_listing_silently_is_an_error()
    {
        var size = PytskFilesystemParser.ListingPageSize;
        var noEntries = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(Page(0, size, size, done: false), """{"next_cursor": 600}"""));
        var act = () => Collect(new PytskFilesystemParser(noEntries.Open).EnumerateAsync("img.dd"));
        (await act.Should().ThrowAsync<SidecarException>()).WithMessage("*incomplete*");

        var stuck = new FakeChannelFactory(() => FakeSidecarChannel.Sequence(Page(0, size, 0, done: false)));
        var loop = () => Collect(new PytskFilesystemParser(stuck.Open).EnumerateAsync("img.dd"));
        (await loop.Should().ThrowAsync<SidecarException>()).WithMessage("*did not advance*");
    }

    [Fact]
    public async Task Entry_fields_are_mapped_and_a_null_timestamp_stays_null()
    {
        var factory = FakeChannelFactory.Returning("""
            {"entries": [
              {"inode": 64, "path": "/Users/a/evil.exe", "name": "evil.exe", "size": 4096, "is_dir": false, "is_deleted": true,
               "btime": null, "mtime": "2023-11-14T22:13:20+00:00", "atime": "1969-12-31T00:00:00+00:00", "ctime": null,
               "owner": "1000", "group": "1000", "mode": 33188},
              {"inode": "sixty-five", "path": 7, "name": null, "size": "big", "is_dir": "yes", "is_deleted": null,
               "btime": 12, "mtime": "not a time", "atime": [], "ctime": {}, "owner": null, "group": null, "mode": 99999999999}
            ], "next_cursor": 2, "done": true, "errors": 0}
            """);

        var entries = await Collect(new PytskFilesystemParser(factory.Open).EnumerateAsync("img.dd"));

        entries.Should().HaveCount(2, "a row of hostile fields is still a row");
        var evil = entries[0];
        evil.Inode.Should().Be(64);
        evil.Path.Should().Be("/Users/a/evil.exe");
        evil.IsDeleted.Should().BeTrue();
        evil.CreatedUtc.Should().BeNull("the worker reports a timestomped creation time as null; it must not become a date");
        evil.MetadataChangedUtc.Should().BeNull();
        evil.ModifiedUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        evil.AccessedUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(-86400));
        evil.UnixMode.Should().Be(33188);

        var hostile = entries[1];
        hostile.CreatedUtc.Should().BeNull();
        hostile.ModifiedUtc.Should().BeNull();
        hostile.AccessedUtc.Should().BeNull();
        hostile.UnixMode.Should().BeNull();
        hostile.Path.Should().Be("7");
    }

    /// <summary>A stand-in for read_file: serves ranges of <paramref name="content"/> the way the worker does.</summary>
    private static FakeChannelFactory FileServer(byte[] content, long? declaredSize = null, int workerCap = 8 * 1024 * 1024)
        => new(() => new FakeSidecarChannel(call =>
        {
            call.Method.Should().Be("read_file");
            var offset = call.Params["file_offset"]!.GetValue<long>();
            var length = Math.Min(call.Params["length"]!.GetValue<int>(), workerCap);
            var size = declaredSize ?? content.Length;
            var available = (int)Math.Max(0, Math.Min(length, content.Length - offset));
            var chunk = content.AsSpan((int)Math.Min(offset, content.Length), available).ToArray();
            return new JsonObject
            {
                ["data_b64"] = Convert.ToBase64String(chunk),
                ["file_offset"] = offset,
                ["length"] = chunk.Length,
                ["size"] = size,
                ["eof"] = offset + chunk.Length >= size || chunk.Length < Math.Min(length, size - offset),
            };
        }));

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + (i >> 8));
        }
        return data;
    }

    [Fact]
    public async Task A_file_larger_than_one_reply_is_read_in_ranges_and_reassembled()
    {
        // 10 MiB + 5: more than the worker's 8 MiB reply cap, and not a multiple of the range size.
        var content = Pattern(10 * 1024 * 1024 + 5);
        var factory = FileServer(content);
        var parser = new PytskFilesystemParser(factory.Open);

        var data = await parser.ReadFileAsync("img.dd", inode: 77, offsetBytes: 512);

        data.Should().Equal(content);
        var calls = factory.Calls.ToList();
        calls.Count.Should().BeGreaterThan(1);
        calls.Should().OnlyContain(c => c.Params["length"]!.GetValue<int>() <= PytskFilesystemParser.ReadChunkBytes
                                        && c.Params["inode"]!.GetValue<long>() == 77
                                        && c.Params["offset"]!.GetValue<long>() == 512);
        calls.Select(c => c.Params["file_offset"]!.GetValue<long>()).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        factory.Opened.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_caller_supplied_cap_bounds_the_read_and_says_whether_the_file_went_on()
    {
        var content = Pattern(5000);
        var parser = new PytskFilesystemParser(FileServer(content).Open);

        var capped = await parser.ReadFileAsync("img.dd", 1, 0, maxBytes: 1000);
        capped.Data.Should().Equal(content.Take(1000));
        capped.Truncated.Should().BeTrue();
        capped.FileSize.Should().Be(5000);

        var exact = await parser.ReadFileAsync("img.dd", 1, 0, maxBytes: 5000);
        exact.Data.Should().Equal(content);
        exact.Truncated.Should().BeFalse("the file ends exactly at the cap: nothing was left out");

        var roomy = await parser.ReadFileAsync("img.dd", 1, 0, maxBytes: 1_000_000);
        roomy.Data.Should().Equal(content);
        roomy.Truncated.Should().BeFalse();

        var nothing = await parser.ReadFileAsync("img.dd", 1, 0, maxBytes: 0);
        nothing.Data.Should().BeEmpty();
        nothing.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task The_whole_file_overload_refuses_a_file_over_its_limit_instead_of_truncating_it()
    {
        var content = Pattern(5000);
        var parser = new PytskFilesystemParser(FileServer(content).Open) { MaxFileBytes = 4999 };

        var act = () => parser.ReadFileAsync("img.dd", 1);

        (await act.Should().ThrowAsync<IOException>()).WithMessage("*5000 bytes*4999-byte limit*");
        (await new PytskFilesystemParser(FileServer(content).Open) { MaxFileBytes = 5000 }.ReadFileAsync("img.dd", 1))
            .Should().Equal(content);
    }

    [Fact]
    public async Task A_hostile_declared_size_costs_nothing()
    {
        // 2^62 bytes declared, three present.
        var parser = new PytskFilesystemParser(FileServer([1, 2, 3], declaredSize: 1L << 62).Open);

        var result = await parser.ReadFileAsync("img.dd", 1, 0, maxBytes: 1 << 20);

        result.Data.Should().Equal(1, 2, 3);
        result.Truncated.Should().BeFalse();
        result.FileSize.Should().Be(1L << 62);
    }

    [Fact]
    public async Task An_empty_file_and_a_copy_to_a_stream()
    {
        (await new PytskFilesystemParser(FileServer([]).Open).ReadFileAsync("img.dd", 1)).Should().BeEmpty();

        var content = Pattern(9_000_000);
        using var destination = new MemoryStream();
        var (size, truncated) = await new PytskFilesystemParser(FileServer(content).Open)
            .CopyFileToAsync("img.dd", 1, destination);
        destination.ToArray().Should().Equal(content);
        size.Should().Be(content.Length);
        truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Replies_that_are_not_what_was_asked_for_are_rejected()
    {
        var notBase64 = FakeChannelFactory.Returning("""{"data_b64": "***", "size": 3, "eof": true}""");
        var bad = () => new PytskFilesystemParser(notBase64.Open).ReadFileAsync("img.dd", 1);
        await bad.Should().ThrowAsync<SidecarException>();

        var tooMuch = new FakeChannelFactory(() => new FakeSidecarChannel(_ => new JsonObject
        {
            ["data_b64"] = Convert.ToBase64String(new byte[PytskFilesystemParser.ReadChunkBytes + 1]),
            ["size"] = 100,
            ["eof"] = true,
        }));
        var over = () => new PytskFilesystemParser(tooMuch.Open).ReadFileAsync("img.dd", 1);
        (await over.Should().ThrowAsync<SidecarException>()).WithMessage("*bytes for a*-byte request*");
    }

    [Fact]
    public async Task Identify_maps_the_volume_description()
    {
        var factory = FakeChannelFactory.Returning("""
            {"kind": "Ntfs", "label": null, "volume_size": 499999997952, "cluster_size": 4096,
             "extras": {"block_count": 122070312, "first_block": 0, "last_block": 122070311}}
            """);

        var info = await new PytskFilesystemParser(factory.Open).IdentifyAsync("img.dd", 2048 * 512);

        info.Kind.Should().Be(FilesystemKind.Ntfs);
        info.Label.Should().BeNull();
        info.VolumeSize.Should().Be(499999997952);
        info.ClusterSize.Should().Be(4096);
        info.Extras!["last_block"].Should().Be("122070311");
        factory.Calls.Single().Params["offset"]!.GetValue<long>().Should().Be(2048 * 512);
    }
}
