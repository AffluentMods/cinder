using Cinder.Native;
using FluentAssertions;
using Xunit;

namespace Cinder.Native.Tests;

public sealed class PlatformContractTests
{
    [Fact]
    public void PlatformInfo_record_round_trips_values()
    {
        var info = new PlatformInfo("TestOS", "x64", "1.0");
        info.Os.Should().Be("TestOS");
        info.Architecture.Should().Be("x64");
        info.OsVersion.Should().Be("1.0");
    }
}
