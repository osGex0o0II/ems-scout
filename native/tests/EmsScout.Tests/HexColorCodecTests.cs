using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class HexColorCodecTests
{
    [Theory]
    [InlineData("#9AA0A6", 255, 154, 160, 166, "#FF9AA0A6")]
    [InlineData("#FFE9C46A", 255, 233, 196, 106, "#FFE9C46A")]
    [InlineData("#7F123456", 127, 18, 52, 86, "#7F123456")]
    public void ValidRgbAndArgbRoundTripWithoutChangingChannels(
        string input,
        byte alpha,
        byte red,
        byte green,
        byte blue,
        string expected)
    {
        var decoded = HexColorCodec.Decode(input, new RgbaColor(255, 1, 2, 3));

        Assert.Equal(new RgbaColor(alpha, red, green, blue), decoded);
        Assert.Equal(expected, HexColorCodec.Encode(decoded));
    }

    [Fact]
    public void InvalidColorUsesTheProvidedFallback()
    {
        var fallback = new RgbaColor(255, 128, 128, 128);

        Assert.Equal(fallback, HexColorCodec.Decode("#12XX56", fallback));
    }
}
