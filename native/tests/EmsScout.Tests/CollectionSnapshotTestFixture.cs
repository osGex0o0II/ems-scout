using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Importing;

namespace EmsScout.Tests;

internal sealed record SnapshotFixtureBuilding(
    string Building,
    string DeviceName,
    string PageName = "default",
    string Communication = "关机",
    int RawCount = 1,
    string? DeviceUid = null,
    double Floor = 1,
    int SubAreaIndex = 0);

internal static class CollectionSnapshotTestFixture
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Write(
        string directory,
        string workflowId,
        params SnapshotFixtureBuilding[] fixtures)
    {
        Directory.CreateDirectory(directory);
        var buildings = new JsonArray(fixtures.Select(BuildingNode).ToArray());
        var root = new JsonObject
        {
            ["contractVersion"] = CollectionSnapshotContractV1.Version,
            ["workflowId"] = workflowId,
            ["completedAt"] = "2026-07-11T00:00:00.000Z",
            ["scope"] = new JsonObject
            {
                ["mode"] = "full",
                ["buildings"] = new JsonArray(fixtures.Select(item => JsonValue.Create(item.Building)).ToArray()),
                ["targets"] = new JsonArray(),
            },
            ["lineage"] = new JsonObject
            {
                ["baseArtifactSha256"] = null,
                ["parentWorkflowId"] = null,
            },
            ["versions"] = new JsonObject
            {
                ["collector"] = "test/1",
                ["playwright"] = "test",
                ["rules"] = "test",
                ["databaseSchema"] = "v2-identity",
                ["sourceRevision"] = "fixture",
            },
            ["counts"] = new JsonObject
            {
                ["buildingCount"] = fixtures.Length,
                ["subAreaCount"] = fixtures.Length,
                ["pageCount"] = fixtures.Length,
                ["rawCardCount"] = fixtures.Sum(item => item.RawCount),
                ["uniqueCardCount"] = fixtures.Length,
            },
            ["quality"] = new JsonObject
            {
                ["decision"] = "accepted",
                ["findings"] = new JsonArray(),
                ["retries"] = new JsonArray(),
            },
            ["artifact"] = new JsonObject
            {
                ["hashScope"] = CollectionSnapshotContractV1.ArtifactHashScope,
                ["sha256"] = new string('0', 64),
                ["bytes"] = 0,
            },
            ["buildings"] = buildings,
        };
        RecomputeArtifact(root);
        var path = Path.Combine(directory, workflowId + ".json");
        File.WriteAllText(path, root.ToJsonString(OutputJson));
        return path;
    }

    public static JsonObject ReadNode(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public static void WriteNode(string path, JsonObject root, bool recomputeArtifact)
    {
        if (recomputeArtifact) RecomputeArtifact(root);
        File.WriteAllText(path, root.ToJsonString(OutputJson));
    }

    public static void RecomputeArtifact(JsonObject root)
    {
        using var document = JsonDocument.Parse(root["buildings"]!.ToJsonString());
        var payload = CollectionSnapshotCanonicalJson.SerializeBuildings(document.RootElement);
        var artifact = root["artifact"]!.AsObject();
        artifact["hashScope"] = CollectionSnapshotContractV1.ArtifactHashScope;
        artifact["sha256"] = CollectionSnapshotCanonicalJson.ComputeSha256(payload);
        artifact["bytes"] = payload.LongLength;
    }

    private static JsonObject BuildingNode(SnapshotFixtureBuilding fixture)
    {
        var identity = new DeviceSourceIdentity(
            fixture.Building,
            fixture.SubAreaIndex,
            fixture.PageName,
            fixture.DeviceName,
            1);
        var sourceKey = DeviceIdentityKeyBuilder.BuildSourceKey(identity);
        var duplicateSourceKeys = Enumerable.Range(1, fixture.RawCount)
            .Select(occurrence => JsonValue.Create(DeviceIdentityKeyBuilder.BuildSourceKey(identity with { Occurrence = occurrence })))
            .ToArray();
        var duplicates = fixture.RawCount > 1
            ? new JsonArray(new JsonObject
            {
                ["name"] = fixture.DeviceName,
                ["copies"] = fixture.RawCount,
                ["sourceKeys"] = new JsonArray(duplicateSourceKeys),
            })
            : new JsonArray();
        var card = new JsonObject
        {
            ["sourceKey"] = sourceKey,
            ["deviceUid"] = fixture.DeviceUid,
            ["name"] = fixture.DeviceName,
            ["switch"] = fixture.Communication == "开机" ? "ON" : "OFF",
            ["mode"] = "制冷",
            ["indoor"] = 26.5,
            ["setTemp"] = 26,
            ["fan"] = "自动",
            ["indicator"] = "indicator.png",
            ["comm"] = fixture.Communication,
            ["sourceEvidence"] = new JsonObject
            {
                ["raw"] = new JsonObject
                {
                    ["name"] = fixture.DeviceName,
                    ["switch"] = fixture.Communication == "开机" ? "ON" : "OFF",
                    ["mode"] = "制冷",
                    ["indoor"] = "26.5",
                    ["setTemp"] = "26",
                    ["fan"] = "自动",
                    ["indicator"] = "indicator.png",
                    ["comm"] = fixture.Communication,
                },
                ["nameFloor"] = 1,
            },
        };
        var pageSourceKey = $"building:{fixture.Building}/sub:{fixture.SubAreaIndex}/page:{fixture.PageName}";
        var page = new JsonObject
        {
            ["sourceKey"] = pageSourceKey,
            ["page"] = fixture.PageName,
            ["rawCount"] = fixture.RawCount,
            ["uniqueCount"] = 1,
            ["duplicates"] = duplicates,
            ["layout"] = "grid",
            ["quality"] = new JsonObject
            {
                ["decision"] = "accepted",
                ["reason"] = "quality_pass",
                ["attempts"] = 1,
            },
            ["sourceEvidence"] = new JsonObject
            {
                ["count"] = fixture.RawCount,
                ["onHref"] = null,
                ["offHref"] = null,
                ["qualityReason"] = "quality_pass",
                ["duplicateNames"] = new JsonArray(),
                ["err"] = null,
            },
            ["cards"] = new JsonArray(card),
        };
        var subSourceKey = $"building:{fixture.Building}/sub:{fixture.SubAreaIndex}";
        var subArea = new JsonObject
        {
            ["sourceKey"] = subSourceKey,
            ["idx"] = fixture.SubAreaIndex,
            ["floor"] = fixture.Floor,
            ["floorLabel"] = fixture.Floor < 0 ? $"B{Math.Abs(fixture.Floor):0.#}F" : $"{fixture.Floor:0.#}F",
            ["text"] = "fixture-area",
            ["x"] = 10,
            ["y"] = 20,
            ["sourceEvidence"] = new JsonObject { ["err"] = null },
            ["pages"] = new JsonArray(page),
        };
        return new()
        {
            ["sourceKey"] = "building:" + fixture.Building,
            ["building"] = fixture.Building,
            ["menuClicked"] = fixture.Building + "楼",
            ["subAreaCount"] = 1,
            ["subAreas"] = new JsonArray(subArea),
        };
    }
}
