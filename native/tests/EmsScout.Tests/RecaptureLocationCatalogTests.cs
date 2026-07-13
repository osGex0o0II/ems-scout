using EmsScout.Application.Collection;

namespace EmsScout.Tests;

public sealed class RecaptureLocationCatalogTests
{
    [Theory]
    [InlineData("5号", 400, "A座")]
    [InlineData("5号", 401, "B座")]
    [InlineData("5号", 616, "B座")]
    [InlineData("5号", 617, "C座")]
    [InlineData("5号", 874, "C座")]
    [InlineData("5号", 875, "D座")]
    [InlineData("5号", 1120, "D座")]
    [InlineData("5号", 1121, "E座")]
    [InlineData("5号", 1424, "E座")]
    [InlineData("5号", 1425, "F座")]
    [InlineData("6号", 650, "A座")]
    [InlineData("6号", 651, "B座")]
    [InlineData("6号", 1220, "B座")]
    [InlineData("6号", 1221, "C座")]
    [InlineData("1号", 999, "整栋")]
    public void SeatUsesTheCollectorCoordinateBoundaries(string building, int x, string expected)
    {
        Assert.Equal(expected, RecaptureLocationCatalog.ResolveSeat(building, x));
    }

    [Fact]
    public void TargetsCanStopAtBuildingSeatOrFloorAndAreDeduplicated()
    {
        RecaptureLocation[] locations =
        [
            new("5号", "A座", "1F", 1, 100, 10),
            new("5号", "A座", "2F", 2, 200, 20),
            new("5号", "A座", "2F", 2, 200, 20),
            new("5号", "B座", "2F", 2, 500, 50),
            new("6号", "A座", "1F", 1, 100, 10),
        ];

        Assert.Equal(
            "5号:100:10,5号:200:20,5号:500:50",
            RecaptureLocationCatalog.BuildTargetArgument(locations, "5号", null, null));
        Assert.Equal(
            "5号:100:10,5号:200:20",
            RecaptureLocationCatalog.BuildTargetArgument(locations, "5号", "A座", null));
        Assert.Equal(
            "5号:200:20",
            RecaptureLocationCatalog.BuildTargetArgument(locations, "5号", "A座", "2F"));
    }

    [Fact]
    public void CascadingOptionsOnlyContainValuesAvailableUnderTheParentSelection()
    {
        RecaptureLocation[] locations =
        [
            new("5号", "A座", "1F", 1, 100, 10),
            new("5号", "B座", "2F", 2, 500, 50),
            new("6号", "A座", "3F", 3, 100, 10),
        ];

        Assert.Equal(["5号", "6号"], RecaptureLocationCatalog.BuildingOptions(locations).Select(item => item.Value));
        Assert.Equal(["", "A座", "B座"], RecaptureLocationCatalog.SeatOptions(locations, "5号").Select(item => item.Value));
        Assert.Equal(["", "1F"], RecaptureLocationCatalog.FloorOptions(locations, "5号", "A座").Select(item => item.Value));
    }
}
