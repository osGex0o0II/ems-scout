using EmsScout.Application.Devices;

namespace EmsScout.Tests;

public sealed class DeviceIdentityKeyBuilderTests
{
    [Fact]
    public void BuildsStableVersionedIdentityVectors()
    {
        var sourceKey = DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity("1号", 22, "三页", "22F-2201-KT"));

        Assert.Equal("sk1_008f1bd84aaf9b34bc00e0b7ed9c93c17b0b6018e8f2868922aff630ff07c6d7", sourceKey);
        Assert.Equal(
            "duid1_9e910fbee3e28dc24eb040c4dbf6c77208eb58d88dd78002d5aeaa8e301cf1c1",
            DeviceIdentityKeyBuilder.CreateInitialDeviceUid(sourceKey));
    }

    [Fact]
    public void DuplicateObservationsHaveUniqueSourceKeysButShareInitialDeviceUid()
    {
        var first = new DeviceSourceIdentity("1号", 22, "三页", "22F-2201-KT");
        var duplicate = first with { Occurrence = 2 };

        Assert.Equal(
            "sk1_815705cef655f8ddc212194d9b0d240b0445df44894e81201d2461df1da60a1e",
            DeviceIdentityKeyBuilder.BuildSourceKey(duplicate));
        Assert.NotEqual(
            DeviceIdentityKeyBuilder.BuildSourceKey(first),
            DeviceIdentityKeyBuilder.BuildSourceKey(duplicate));
        Assert.Equal(
            DeviceIdentityKeyBuilder.CreateInitialDeviceUid(first),
            DeviceIdentityKeyBuilder.CreateInitialDeviceUid(duplicate));
    }

    [Fact]
    public void CanonicalizesWhitespaceCaseAndUnicodeComposition()
    {
        var composed = DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity(" b1 ", 3, " Page-A ", "CAFÉ-KT"));
        var decomposed = DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity("B1", 3, "page-a", "CAFE\u0301-KT"));

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void LengthPrefixesPreventComponentBoundaryCollisions()
    {
        var first = DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity("A|B", 12, "C", "D"));
        var second = DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity("A", 12, "B|C", "D"));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RejectsInvalidComponentsAndMalformedKeys()
    {
        Assert.Throws<ArgumentException>(() => DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity(" ", 1, "default", "A-KT")));
        Assert.Throws<ArgumentException>(() => DeviceIdentityKeyBuilder.CreateInitialDeviceUid("sk1_not-a-hash"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceIdentityKeyBuilder.BuildSourceKey(
            new DeviceSourceIdentity("1号", 1, "default", "A-KT", 0)));
        Assert.False(DeviceIdentityKeyBuilder.IsDeviceUid("duid1_ABC"));
    }
}
