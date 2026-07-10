using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class InventorySummarizerTests
{
    [Theory]
    [InlineData("开机", DeviceCommunicationState.Running)]
    [InlineData("关机", DeviceCommunicationState.Stopped)]
    [InlineData("离线", DeviceCommunicationState.Offline)]
    [InlineData("", DeviceCommunicationState.Unknown)]
    public void ParsesLegacyCommunicationState(string value, DeviceCommunicationState expected)
    {
        Assert.Equal(expected, DeviceCommunicationStateParser.Parse(value));
    }

    [Fact]
    public void SummarizesByBuildingInBuildingOrder()
    {
        var cards = new[]
        {
            Card("6号", DeviceCommunicationState.Offline),
            Card("1号", DeviceCommunicationState.Running),
            Card("1号", DeviceCommunicationState.Stopped),
        };

        var summary = new InventorySummarizer().Summarize(cards);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Running);
        Assert.Equal(1, summary.Stopped);
        Assert.Equal(1, summary.Offline);
        Assert.Collection(
            summary.Buildings,
            first => Assert.Equal("1号", first.Building),
            second => Assert.Equal("6号", second.Building));
    }

    private static AirConditionerCard Card(string building, DeviceCommunicationState state)
    {
        return new AirConditionerCard(
            Building: building,
            SubArea: "1F",
            Floor: 1,
            Page: "default",
            Name: "KT-1",
            SwitchState: "-",
            Mode: "",
            IndoorTemperature: null,
            SetTemperature: null,
            Fan: "",
            Indicator: "",
            CommunicationState: state);
    }
}
