namespace EmsScout.Application.Collection;

public sealed record CollectionRunFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Building = null,
    string? Source = null,
    string? Status = null,
    string? Keyword = null)
{
    public bool Matches(CollectionRunRecord run)
    {
        var timestamp = ParseTimestamp(run.CompletedAt);
        if (From.HasValue && timestamp < From.Value)
        {
            return false;
        }

        if (To.HasValue && timestamp > To.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Building) &&
            !run.Buildings.Contains(Building.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Source) &&
            !string.Equals(CollectionRunMetadata.From(run).Source, Source.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Status) &&
            !string.Equals(run.Status, Status.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Keyword))
        {
            var keyword = Keyword.Trim();
            var matches = new[] { run.RunKey, run.Note, run.Scope, run.Source, run.DataVersion }
                .Any(value => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
