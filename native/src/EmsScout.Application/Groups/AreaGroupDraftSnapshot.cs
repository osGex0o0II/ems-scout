namespace EmsScout.Application.Groups;

public sealed class AreaGroupDraftSnapshot : IEquatable<AreaGroupDraftSnapshot>
{
    public AreaGroupDraftSnapshot(
        AreaGroupEdit group,
        IEnumerable<AreaGroupRuleEdit> rules)
    {
        Group = group;
        Rules = rules.ToArray();
    }

    public AreaGroupEdit Group { get; }

    public IReadOnlyList<AreaGroupRuleEdit> Rules { get; }

    public bool Equals(AreaGroupDraftSnapshot? other)
    {
        return other is not null &&
            Group == other.Group &&
            Rules.SequenceEqual(other.Rules);
    }

    public override bool Equals(object? obj) => Equals(obj as AreaGroupDraftSnapshot);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Group);
        foreach (var rule in Rules)
            hash.Add(rule);
        return hash.ToHashCode();
    }
}
