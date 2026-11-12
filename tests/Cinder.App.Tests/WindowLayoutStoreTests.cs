using Xunit;
using Cinder.App.Services;
using FluentAssertions;

namespace Cinder.App.Tests;

public class WindowLayoutStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cinder-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_and_creates_the_directory()
    {
        var store = new WindowLayoutStore(Path.Combine(_dir, "nested", "window.json"));
        var layout = new WindowLayout(10, 20, 1400, 900, true, 250, 320, true, false);
        store.Save(layout);
        store.Load().Should().Be(layout);
    }

    [Fact]
    public void Missing_or_corrupt_file_yields_null()
    {
        var path = Path.Combine(_dir, "window.json");
        new WindowLayoutStore(path).Load().Should().BeNull();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{not json");
        new WindowLayoutStore(path).Load().Should().BeNull();
    }

    [Fact]
    public void Implausible_sizes_are_rejected()
    {
        var store = new WindowLayoutStore(Path.Combine(_dir, "window.json"));
        store.Save(new WindowLayout(0, 0, 10, 10, false, 232, 300, true, true));
        store.Load().Should().BeNull();
        store.Save(new WindowLayout(0, 0, 100_000, 900, false, 232, 300, true, true));
        store.Load().Should().BeNull();
    }
}
