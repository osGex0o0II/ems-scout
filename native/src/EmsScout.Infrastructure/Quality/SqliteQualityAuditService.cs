using System.Globalization;
using EmsScout.Application.Quality;

namespace EmsScout.Infrastructure.Quality;

public sealed class SqliteQualityAuditService : INativeQualityAuditService
{
    private readonly Func<string> _databasePathResolver;
    private readonly Func<string?> _knownFindingsPathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly SqliteQualityAuditDataReader _reader = new();
    private readonly QualityAuditAnalyzer _analyzer = new();

    public SqliteQualityAuditService(
        Func<string> databasePathResolver,
        Func<string?>? knownFindingsPathResolver = null,
        TimeProvider? timeProvider = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _knownFindingsPathResolver = knownFindingsPathResolver ?? (() => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
        AuditAsync(NativeQualityAuditRequest.LatestCompletedRun, cancellationToken);

    public async Task<QualityAuditReport?> AuditAsync(
        NativeQualityAuditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolvedPath = request.DatabasePath ?? _databasePathResolver();
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            throw new InvalidOperationException("The SQLite quality audit database path is empty.");
        }

        var databasePath = Path.GetFullPath(resolvedPath);
        var data = await _reader
            .ReadAsync(databasePath, request.SourceKind, cancellationToken)
            .ConfigureAwait(false);
        if (data is null)
        {
            return null;
        }

        var knownFindings = await KnownQualityFindingCatalog
            .LoadAsync(_knownFindingsPathResolver(), cancellationToken)
            .ConfigureAwait(false);
        var analysis = _analyzer.Analyze(data, knownFindings);
        var generatedAt = _timeProvider.GetUtcNow();
        return new QualityAuditReport(
            SourcePath: databasePath,
            GeneratedAt: generatedAt.ToString("O", CultureInfo.InvariantCulture),
            GeneratedAtLocal: generatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            RunId: data.RunId,
            Summary: analysis.Summary,
            Issues: analysis.Issues,
            IsStale: false,
            StaleReason: string.Empty)
        {
            KnownFindingAnnotations = analysis.KnownFindingAnnotations,
        };
    }
}
