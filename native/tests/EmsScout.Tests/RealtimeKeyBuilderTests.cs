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

    [Theory]
    [InlineData("裙楼/一页")]
    [InlineData("塔楼/一页")]
    public void ExactKeyNormalizesBuildingPagePrefix(string databasePageName)
    {
        var databaseKey = RealtimeKeyBuilder.ExactKey(
            "1号", 2, "2F", databasePageName, "QL-WSJ-KT-1");
        var realtimeKey = RealtimeKeyBuilder.ExactKey(
            "1号", 2, "2F", "一页", "QL-WSJ-KT-1");

        Assert.Equal(realtimeKey, databaseKey);
    }
}
