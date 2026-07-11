using EmsScout.Infrastructure.Importing;

namespace EmsScout.Tests;

public sealed class CollectionSnapshotReaderContractTests
{
    [Fact]
    public async Task ReadsCanonicalArtifactAndPreservesRawUniqueDuplicateCounts()
    {
        var root = TempDirectory();
        var path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-valid",
            new SnapshotFixtureBuilding("1号", "1-0101-KT", RawCount: 2));

        var result = await new CollectionSnapshotReader().ReadAsync(path);

        Assert.True(result.ArtifactVerification.IsValid);
        Assert.Equal(2, result.Snapshot.Counts.RawCardCount);
        Assert.Equal(1, result.Snapshot.Counts.UniqueCardCount);
        Assert.Single(result.Snapshot.Buildings[0].SubAreas[0].Pages[0].Cards);
        Assert.Equal(2, result.Snapshot.Buildings[0].SubAreas[0].Pages[0].Duplicates[0].SourceKeys.Count);
    }

    [Fact]
    public async Task RejectsArtifactMutationBeforeDatabaseAccess()
    {
        var root = TempDirectory();
        var path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-hash",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var node = CollectionSnapshotTestFixture.ReadNode(path);
        node["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!["comm"] = "开机";
        CollectionSnapshotTestFixture.WriteNode(path, node, recomputeArtifact: false);

        var error = await Assert.ThrowsAsync<CollectionSnapshotContractException>(
            () => new CollectionSnapshotReader().ReadAsync(path));

        Assert.Contains("artifact mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUnknownFieldsMissingRequiredNullablesEnumsAndSourceKeyDrift()
    {
        var root = TempDirectory();
        var path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-shape",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));

        var unknown = CollectionSnapshotTestFixture.ReadNode(path);
        unknown["extra"] = true;
        CollectionSnapshotTestFixture.WriteNode(path, unknown, recomputeArtifact: false);
        await Assert.ThrowsAsync<CollectionSnapshotContractException>(() => new CollectionSnapshotReader().ReadAsync(path));

        path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-nullable",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var missingNullable = CollectionSnapshotTestFixture.ReadNode(path);
        missingNullable["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!.AsObject().Remove("deviceUid");
        CollectionSnapshotTestFixture.WriteNode(path, missingNullable, recomputeArtifact: true);
        await Assert.ThrowsAsync<CollectionSnapshotContractException>(() => new CollectionSnapshotReader().ReadAsync(path));

        path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-enum",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var invalidEnum = CollectionSnapshotTestFixture.ReadNode(path);
        invalidEnum["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!["switch"] = "BROKEN";
        CollectionSnapshotTestFixture.WriteNode(path, invalidEnum, recomputeArtifact: true);
        await Assert.ThrowsAsync<CollectionSnapshotContractException>(() => new CollectionSnapshotReader().ReadAsync(path));

        path = CollectionSnapshotTestFixture.Write(
            root,
            "reader-key",
            new SnapshotFixtureBuilding("1号", "1-0101-KT"));
        var invalidKey = CollectionSnapshotTestFixture.ReadNode(path);
        invalidKey["buildings"]![0]!["subAreas"]![0]!["pages"]![0]!["cards"]![0]!["sourceKey"] = "sk1_" + new string('0', 64);
        CollectionSnapshotTestFixture.WriteNode(path, invalidKey, recomputeArtifact: true);
        var keyError = await Assert.ThrowsAsync<CollectionSnapshotContractException>(() => new CollectionSnapshotReader().ReadAsync(path));
        Assert.Contains("sourceKey mismatch", keyError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ems-snapshot-reader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
