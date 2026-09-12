using System.Globalization;
using EmsScout.Application;

namespace EmsScout.Tests;

public sealed class StoredTimestampTests
{
    [Fact]
    public void NormalizesExplicitOffsetToUtc()
    {
        Assert.True(StoredTimestamp.TryParse("2026-08-31T11:00:00+08:00", out var timestamp));

        Assert.Equal("2026-08-31T03:00:00.0000000+00:00", timestamp.ToString("O"));
    }

    [Fact]
    public void TreatsLegacyTimestampWithoutOffsetAsUtcOnce()
    {
        Assert.True(StoredTimestamp.TryParse("2026-08-31T03:00:00", out var timestamp));

        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.Equal(3, timestamp.Hour);
    }

    [Fact]
    public void RejectsBlankOrInvalidTimestamp()
    {
        Assert.False(StoredTimestamp.TryParse(" ", out _));
        Assert.False(StoredTimestamp.TryParse("not-a-timestamp", out _));
    }

    [Fact]
    public void FormatsAnyOffsetUsingLocalComputerTimeAndOffset()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-31T11:00:00+08:00");
        var expected = timestamp.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

        Assert.Equal(expected, StoredTimestamp.FormatLocal(timestamp));
    }
}
