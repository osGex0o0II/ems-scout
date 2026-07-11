using System.Text.Json;
using System.Text.Json.Serialization;
using EmsScout.Application.Quality;

namespace EmsScout.Infrastructure.Quality;

internal sealed class KnownQualityFindingCatalog(IReadOnlyList<KnownQualityFinding> findings)
{
    private static readonly HashSet<string> NonBlockingStatuses =
    [
        "accepted_ems_source_defect",
        "accepted_source_state",
        "accepted_long_offline",
    ];

    public static async Task<KnownQualityFindingCatalog> LoadAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new KnownQualityFindingCatalog([]);
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<KnownFindingDocument>(
                stream,
                KnownFindingJsonContext.Default.KnownFindingDocument,
                cancellationToken)
            .ConfigureAwait(false);
        var rows = document?.Findings ?? [];
        return new KnownQualityFindingCatalog(rows);
    }

    public KnownFindingClassification<T> Classify<T>(
        IReadOnlyList<T> rows,
        string issueCode,
        Func<T, QualityFindingSubject> subjectSelector)
    {
        var blocking = new List<T>();
        var annotations = new List<QualityAuditKnownFindingAnnotation>();
        foreach (var row in rows)
        {
            var subject = subjectSelector(row);
            var matches = findings
                .Where(finding => Matches(subject, issueCode, finding))
                .ToList();
            if (matches.Count == 0)
            {
                blocking.Add(row);
                continue;
            }

            var references = matches
                .Select(finding => new QualityAuditKnownFindingReference(
                    finding.Id ?? string.Empty,
                    finding.Type ?? string.Empty,
                    finding.Status ?? string.Empty,
                    !NonBlockingStatuses.Contains(finding.Status ?? string.Empty),
                    finding.Reason ?? string.Empty,
                    finding.Evidence ?? []))
                .ToList();
            var annotationIsBlocking = references.All(reference => reference.IsBlocking);
            if (annotationIsBlocking)
            {
                blocking.Add(row);
            }

            annotations.Add(new QualityAuditKnownFindingAnnotation(
                issueCode,
                subject.Building ?? string.Empty,
                subject.Floor,
                subject.SubArea ?? string.Empty,
                subject.PageName ?? string.Empty,
                subject.DeviceName ?? string.Empty,
                annotationIsBlocking,
                references));
        }

        return new KnownFindingClassification<T>(blocking, annotations);
    }

    private static bool Matches(
        QualityFindingSubject subject,
        string issueCode,
        KnownQualityFinding finding)
    {
        if (!SameText(subject.Building, finding.Building) ||
            !SameNumber(subject.Floor, finding.Floor) ||
            !SameText(subject.SubArea, finding.SubArea) ||
            !SameText(subject.PageName, finding.Page) ||
            !SameNumber(subject.X, finding.X) ||
            !SameNumber(subject.Y, finding.Y))
        {
            return false;
        }

        return issueCode switch
        {
            "offline_template_stable" or "offline_template_without_stability" =>
                finding.Type == "offline_template_page",
            "invalid_card_fields" =>
                finding.Type == "device_invalid_fields" && SameText(subject.DeviceName, finding.Device),
            "active_field_incomplete_pages" => finding.Type == "device_invalid_fields",
            "unknown_comm" or "missing_indicator" =>
                finding.Type == "device_missing_indicator" && SameText(subject.DeviceName, finding.Device),
            _ => false,
        };
    }

    private static bool SameText(string? actual, string? expected) =>
        string.IsNullOrEmpty(expected) || string.Equals(actual ?? string.Empty, expected, StringComparison.Ordinal);

    private static bool SameNumber(double? actual, double? expected) =>
        expected is null || actual == expected;
}

internal sealed record QualityFindingSubject(
    string? Building,
    double? Floor,
    string? SubArea,
    string? PageName,
    double? X,
    double? Y,
    string? DeviceName = null);

internal sealed record KnownFindingClassification<T>(
    IReadOnlyList<T> BlockingRows,
    IReadOnlyList<QualityAuditKnownFindingAnnotation> Annotations);

internal sealed class KnownFindingDocument
{
    [JsonPropertyName("findings")]
    public List<KnownQualityFinding>? Findings { get; init; }
}

internal sealed class KnownQualityFinding
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("building")]
    public string? Building { get; init; }

    [JsonPropertyName("floor")]
    public double? Floor { get; init; }

    [JsonPropertyName("subArea")]
    public string? SubArea { get; init; }

    [JsonPropertyName("page")]
    public string? Page { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("evidence")]
    public List<string>? Evidence { get; init; }
}

[JsonSerializable(typeof(KnownFindingDocument))]
internal partial class KnownFindingJsonContext : JsonSerializerContext;
