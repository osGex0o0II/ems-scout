using System.Text.Json;
using EmsScout.Infrastructure.Quality;

namespace EmsScout.Tests;

public sealed class CollectionPageQualityContractTests
{
    [Fact]
    public async Task NativeRulesMatchTheSharedV1Contract()
    {
        var fixturePath = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "quality",
            "page-quality-v1.json");
        await using var stream = File.OpenRead(fixturePath);
        var fixture = await JsonSerializer.DeserializeAsync<QualityFixture>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(fixture);
        Assert.Equal(1, fixture.Version);
        foreach (var item in fixture.Cases)
        {
            var actual = CollectionPageQualityRules.Evaluate(item.Cards, item.Meta);
            Assert.Equal(item.Expected, actual);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(current.FullName, "native")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate EMS repository root for shared quality fixtures.");
    }

    private sealed record QualityFixture(int Version, IReadOnlyList<QualityCase> Cases);

    private sealed record QualityCase(
        string Id,
        IReadOnlyList<CollectionPageQualityCard> Cards,
        CollectionPageQualityMeta? Meta,
        CollectionPageQualityResult Expected);
}
