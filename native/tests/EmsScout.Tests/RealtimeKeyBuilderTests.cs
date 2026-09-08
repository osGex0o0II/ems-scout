using EmsScout.Application.Devices;

namespace EmsScout.Tests;

public sealed class RealtimeKeyBuilderTests
{
    [Fact]
    public void DuplicateSuffixUsesTheSameSourceIdentityAsRealtimeData()
    {
        var baseKey = RealtimeKeyBuilder.NameKey("1号", "1-0101-KT");
        var duplicateKey = RealtimeKeyBuilder.NameKey("1号", "1-0101-KT#2");

        Assert.Equal(baseKey, duplicateKey);
        Assert.Equal("1-0101-KT", RealtimeKeyBuilder.SourceCardName("1-0101-KT#2"));
    }
}
