using EmsScout.Application.Groups;

namespace EmsScout.Tests;

public sealed class AreaGroupDraftSnapshotTests
{
    [Fact]
    public void SnapshotsUseValueEqualityForEquivalentRuleCollections()
    {
        var group = Edit(17, "公区");
        var first = new AreaGroupDraftSnapshot(group, [Rule("GQ | WSJ")]);
        var second = new AreaGroupDraftSnapshot(group with { }, [Rule("GQ | WSJ")]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SnapshotsDetectRuleAddAndRemoveChanges()
    {
        var group = Edit(17, "公区");
        var saved = new AreaGroupDraftSnapshot(group, []);
        var changed = new AreaGroupDraftSnapshot(group, [Rule("GQ")]);
        var restored = new AreaGroupDraftSnapshot(group, []);

        Assert.NotEqual(saved, changed);
        Assert.Equal(saved, restored);
    }

    private static AreaGroupEdit Edit(long id, string name) =>
        new(id, name, string.Empty, "公共区域设备", "重点", true, "public");

    private static AreaGroupRuleEdit Rule(string keywords) =>
        new(0, "1号", "-", "-", AreaGroupRuleNormalizer.Include, keywords, string.Empty, 3, 1);
}
