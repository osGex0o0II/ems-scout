namespace EmsScout.Application.Collection;

public sealed record CollectionRunMetadata(
    string Source,
    string DataVersion,
    string Operator,
    bool IsCurrent)
{
    public static CollectionRunMetadata From(CollectionRunRecord run, bool isCurrent = false) =>
        new(
            Normalize(run.Source, "采集导入"),
            Normalize(run.DataVersion, "v1.0.0"),
            Normalize(run.Operator, "本机"),
            isCurrent);

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
