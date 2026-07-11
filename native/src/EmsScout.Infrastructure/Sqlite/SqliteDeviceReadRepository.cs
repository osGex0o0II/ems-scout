using System.Globalization;
using EmsScout.Application.Devices;
using EmsScout.Application.Watch;
using EmsScout.Domain;
using Microsoft.Data.Sqlite;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteDeviceReadRepository(
    string databasePath,
    IRealtimeDetailSource? realtimeDetailSource = null,
    IDeviceWatchRepository? watchRepository = null) : IDeviceReadRepository
{
    public SqliteDeviceReadRepository(
        Func<string> databasePathResolver,
        IRealtimeDetailSource? realtimeDetailSource = null,
        IDeviceWatchRepository? watchRepository = null)
        : this(string.Empty, realtimeDetailSource, watchRepository)
    {
        DatabasePathResolver = databasePathResolver;
    }

    private Func<string> DatabasePathResolver { get; } = () => databasePath;

    public async Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default)
    {
        var source = DeviceSqlSource.For(query.RunId);
        var (whereSql, parameters) = BuildWhereClause(query, source);
        var limit = Math.Clamp(query.Limit, 1, 50000);
        var offset = Math.Max(0, query.Offset);

        await using var connection = SqliteDatabase.OpenExisting(
            DatabasePathResolver,
            SqliteOpenMode.ReadOnly,
            SqliteCacheMode.Shared);
        var annotations = source.IsHistory
            ? EmptyAnnotations()
            : await LoadAnnotationMapsAsync(connection, cancellationToken).ConfigureAwait(false);
        var overrides = source.IsHistory
            ? RealtimeMatchOverrideSet.Empty
            : await LoadRealtimeMatchOverridesAsync(connection, cancellationToken).ConfigureAwait(false);
        var rows = await LoadDeviceRowsAsync(connection, source, whereSql, parameters, annotations, cancellationToken).ConfigureAwait(false);

        var realtimeSet = realtimeDetailSource is null || source.IsHistory
            ? new RealtimeDetailSet([])
            : await realtimeDetailSource.LoadAsync(ResolveRealtimeBuildings(query, rows), cancellationToken).ConfigureAwait(false);
        rows = AttachRealtimeRows(rows, realtimeSet, overrides);
        if (!source.IsHistory)
        {
            rows = await AttachWatchRowsAsync(rows, cancellationToken).ConfigureAwait(false);
        }

        var baseFiltered = rows
            .Where(row => DeviceQuerySpecification.MatchesScope(row, query))
            .ToList();
        var facets = DeviceFacets.From(
            baseFiltered,
            realtimeRows: realtimeSet.Rows.Count,
            realtimeUnmatched: realtimeSet.UnmatchedRealtimeRows);
        var filtered = baseFiltered
            .Where(row => DeviceQuerySpecification.MatchesResult(row, query))
            .ToList();
        filtered = SortRows(filtered, query).ToList();
        return new DeviceListResult(
            Total: filtered.Count,
            Rows: filtered.Skip(offset).Take(limit).ToList(),
            Facets: facets);
    }

    private async Task<List<DeviceRecord>> AttachWatchRowsAsync(
        List<DeviceRecord> rows,
        CancellationToken cancellationToken)
    {
        if (watchRepository is null || rows.Count == 0)
        {
            return rows;
        }

        var evaluation = await watchRepository.EvaluateAsync(new DeviceWatchQuery(), cancellationToken).ConfigureAwait(false);
        if (evaluation.DeviceStates.Count == 0)
        {
            return rows;
        }

        return rows
            .Select(row => evaluation.DeviceStates.TryGetValue(DeviceWatchKey.KeyFor(row), out var state)
                ? row with { Watch = state }
                : row)
            .ToList();
    }

    public Task<DeviceFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        return LoadFilterOptionsAsync(new DeviceQuery(), cancellationToken);
    }

    public async Task<DeviceFilterOptions> LoadFilterOptionsAsync(
        DeviceQuery query,
        CancellationToken cancellationToken = default)
    {
        await using var connection = SqliteDatabase.OpenExisting(
            DatabasePathResolver,
            SqliteOpenMode.ReadOnly,
            SqliteCacheMode.Shared);
        var source = DeviceSqlSource.For(query.RunId);
        var (runWhereSql, runParameters) = BuildWhereClause(query, source);

        var annotations = source.IsHistory
            ? EmptyAnnotations()
            : await LoadAnnotationMapsAsync(connection, cancellationToken).ConfigureAwait(false);
        var overrides = source.IsHistory
            ? RealtimeMatchOverrideSet.Empty
            : await LoadRealtimeMatchOverridesAsync(connection, cancellationToken).ConfigureAwait(false);
        var rows = await LoadDeviceRowsAsync(
            connection,
            source,
            runWhereSql,
            runParameters,
            annotations,
            cancellationToken).ConfigureAwait(false);
        var realtimeSet = realtimeDetailSource is null || source.IsHistory
            ? new RealtimeDetailSet([])
            : await realtimeDetailSource.LoadAsync(ResolveRealtimeBuildings(query, rows), cancellationToken).ConfigureAwait(false);
        rows = AttachRealtimeRows(rows, realtimeSet, overrides);
        if (!source.IsHistory)
        {
            rows = await AttachWatchRowsAsync(rows, cancellationToken).ConfigureAwait(false);
        }

        var filtered = rows
            .Where(row => DeviceQuerySpecification.MatchesResult(row, query))
            .ToList();

        return new DeviceFilterOptions(
            CountOptions(filtered, row => row.Building),
            CountOptions(filtered, row => row.CommunicationStatusText, sortByCountDescending: true),
            CountOptions(filtered, row => row.FloorLabel, sortKey: option => FloorSortValue(option.Value)),
            CountOptions(filtered, row => row.SubArea),
            PageOptions(filtered),
            CountOptions(filtered, row => row.Name),
            CountOptions(filtered.Where(ShouldExposeZuo), row => row.Zuo ?? string.Empty),
            CountOptions(filtered, row => row.Mode),
            CountOptions(filtered, row => row.Fan),
            CountOptions(filtered, row => row.SetTemperature),
            CountOptions(filtered, row => row.IndoorTemperature),
            CountOptions(filtered.SelectMany(row => row.TagList), tag => tag),
            RealtimePowers: CountOptions(filtered, row => row.Realtime?.PowerState ?? string.Empty),
            RealtimeModes: CountOptions(filtered, row => row.Realtime?.Mode ?? string.Empty),
            RealtimeFans: CountOptions(filtered, row => row.Realtime?.Fan ?? string.Empty),
            RealtimeLocks: CountOptions(
                filtered,
                row => row.RealtimeLockText),
            RealtimeSystemTypes: CountOptions(filtered, row => row.Realtime?.Field("系统类型") ?? string.Empty));
    }

    private static async Task<List<DeviceRecord>> LoadDeviceRowsAsync(
        SqliteConnection connection,
        DeviceSqlSource source,
        string whereSql,
        IReadOnlyDictionary<string, object> parameters,
        AnnotationMaps annotations,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
              c.id,
              s.building,
              s.floor,
              s.text AS sub_area,
              s.x,
              s.y,
              p.page_name,
              p.layout,
              c.name,
              c.switch,
              c.mode,
              c.indoor,
              c.set_temp,
              c.fan,
              c.indicator,
              c.comm
            {source.FromSql}
            {whereSql}
            ORDER BY s.building, s.floor, s.sub_idx, p.id, c.name
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);

        var rows = new List<DeviceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var floor = SqliteValueReader.ReadNullableDouble(reader, "floor");
            var building = SqliteValueReader.ReadString(reader, "building");
            var name = SqliteValueReader.ReadString(reader, "name");
            var subArea = SqliteValueReader.ReadString(reader, "sub_area");
            var x = SqliteValueReader.ReadNullableDouble(reader, "x");
            var y = SqliteValueReader.ReadNullableDouble(reader, "y");
            var comm = SqliteValueReader.ReadString(reader, "comm");
            var zuo = DeviceZuoClassifier.Classify(building, x);
            var annotationKey = AnnotationKey(building, name);
            rows.Add(new DeviceRecord(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: building,
                Floor: floor,
                FloorLabel: DeviceFloorLabelFormatter.Format(floor, subArea),
                SubArea: subArea,
                X: x,
                Y: y,
                PageName: SqliteValueReader.ReadString(reader, "page_name"),
                Name: name,
                Layout: SqliteValueReader.ReadString(reader, "layout"),
                SwitchState: SqliteValueReader.ReadString(reader, "switch"),
                Mode: SqliteValueReader.ReadString(reader, "mode"),
                IndoorTemperature: SqliteValueReader.ReadString(reader, "indoor"),
                SetTemperature: SqliteValueReader.ReadString(reader, "set_temp"),
                Fan: SqliteValueReader.ReadString(reader, "fan"),
                Indicator: SqliteValueReader.ReadString(reader, "indicator"),
                CommunicationText: comm,
                CommunicationState: DeviceCommunicationStateParser.Parse(comm),
                Zuo: zuo,
                ZuoSource: string.IsNullOrWhiteSpace(zuo) ? string.Empty : "db",
                Note: annotations.Notes.GetValueOrDefault(annotationKey, string.Empty),
                Tags: annotations.Tags.GetValueOrDefault(annotationKey, [])));
        }

        return rows;
    }

    private static (string Sql, IReadOnlyDictionary<string, object> Parameters) BuildWhereClause(
        DeviceQuery query,
        DeviceSqlSource source)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object>();
        if (source.RunId is not null)
        {
            clauses.Add("c.run_id = $run_id");
            parameters["$run_id"] = source.RunId.Value;
        }

        if (!string.IsNullOrWhiteSpace(query.Building))
        {
            clauses.Add("s.building = $building");
            parameters["$building"] = query.Building.Trim();
        }

        AddCommunicationClause(clauses, parameters, query.CommunicationState);
        AddExactTextClause(clauses, parameters, "s.text", query.SubArea, "sub_area");
        AddPageNameClause(clauses, parameters, query.PageName);
        AddContainsTextClause(clauses, parameters, "c.name", query.DeviceName, "device_name");
        AddExactTextClause(clauses, parameters, "c.mode", query.Mode, "mode");
        AddExactTextClause(clauses, parameters, "c.fan", query.Fan, "fan");
        AddExactTextClause(clauses, parameters, "c.set_temp", query.SetTemperature, "set_temp");
        AddExactTextClause(clauses, parameters, "c.indoor", query.IndoorTemperature, "indoor");

        var groupIds = ParseGroupIds(query.MonitorGroupIds);
        if (groupIds.Count > 0)
        {
            var groupClauses = new List<string>();
            for (var i = 0; i < groupIds.Count; i++)
            {
                var parameterName = "$group_id_" + i.ToString(CultureInfo.InvariantCulture);
                parameters[parameterName] = groupIds[i];
                groupClauses.Add(parameterName);
            }

            clauses.Add($"""
                EXISTS (
                    SELECT 1
                    FROM monitor_group_items mgi
                    JOIN monitor_groups mg ON mg.id = mgi.group_id
                    WHERE mgi.group_id IN ({string.Join(",", groupClauses)})
                      AND mg.enabled = 1
                      AND mgi.building = s.building
                      AND (
                        (
                          mgi.target_type = 'device'
                          AND mgi.card_name = c.name
                          AND (
                            (mgi.floor_value IS NULL AND IFNULL(mgi.sub_area_text, '') = '')
                            OR (
                              (mgi.floor_value IS NULL OR ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001)
                              AND (IFNULL(mgi.sub_area_text, '') = '' OR IFNULL(mgi.sub_area_text, '') = IFNULL(s.text, ''))
                            )
                          )
                        )
                        OR (
                          mgi.target_type = 'sub_area'
                          AND ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                          AND IFNULL(mgi.sub_area_text, '') = IFNULL(s.text, '')
                        )
                        OR (
                          mgi.target_type = 'floor'
                          AND ABS(COALESCE(s.floor, -999999) - COALESCE(mgi.floor_value, -999998)) < 0.001
                        )
                      )
                )
                """);
        }

        if (clauses.Count == 0)
        {
            return (string.Empty, parameters);
        }

        return ($"WHERE {string.Join(" AND ", clauses)}", parameters);
    }

    private static void AddCommunicationClause(
        List<string> clauses,
        Dictionary<string, object> parameters,
        string? communicationState)
    {
        if (string.IsNullOrWhiteSpace(communicationState))
        {
            return;
        }

        var expected = communicationState.Trim();
        if (expected.Equals("未知", StringComparison.OrdinalIgnoreCase))
        {
            clauses.Add("IFNULL(NULLIF(TRIM(c.comm), ''), '未知') = '未知'");
            return;
        }

        clauses.Add("IFNULL(NULLIF(TRIM(c.comm), ''), '未知') = $communication");
        parameters["$communication"] = expected;
    }

    private static void AddExactTextClause(
        List<string> clauses,
        Dictionary<string, object> parameters,
        string expression,
        string? value,
        string parameterKey)
    {
        var allowed = ValueList(value).ToList();
        if (allowed.Count == 0)
        {
            return;
        }

        var parameterNames = new List<string>();
        for (var i = 0; i < allowed.Count; i++)
        {
            var parameterName = "$" + parameterKey + "_" + i.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            parameters[parameterName] = allowed[i];
        }

        clauses.Add($"IFNULL(NULLIF(TRIM({expression}), ''), '') IN ({string.Join(",", parameterNames)})");
    }

    private static void AddContainsTextClause(
        List<string> clauses,
        Dictionary<string, object> parameters,
        string expression,
        string? value,
        string parameterKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var parameterName = "$" + parameterKey;
        clauses.Add($"INSTR(LOWER(IFNULL({expression}, '')), LOWER({parameterName})) > 0");
        parameters[parameterName] = value.Trim();
    }

    private static void AddPageNameClause(
        List<string> clauses,
        Dictionary<string, object> parameters,
        string? pageName)
    {
        var allowed = ValueList(pageName)
            .Select(DevicePageNameFormatter.NormalizeValue)
            .ToList();
        if (allowed.Count == 0)
        {
            return;
        }

        var parameterNames = new List<string>();
        for (var i = 0; i < allowed.Count; i++)
        {
            var parameterName = "$page_name_" + i.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            parameters[parameterName] = allowed[i];
        }

        clauses.Add($"IFNULL(NULLIF(TRIM(p.page_name), ''), 'default') IN ({string.Join(",", parameterNames)})");
    }

    private static IReadOnlyList<long> ParseGroupIds(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string> ValueList(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<DeviceRecord> SortRows(IReadOnlyList<DeviceRecord> rows, DeviceQuery query)
    {
        static double TemperatureValue(DeviceRecord row)
        {
            return double.TryParse(
                row.IndoorTemperature,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
                ? value
                : double.MaxValue;
        }

        var sorted = (query.SortBy ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "building" => rows.OrderBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Floor ?? double.MaxValue)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "floor" => rows.OrderBy(row => row.Floor ?? double.MaxValue)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "name" => rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase),
            "comm" => rows.OrderBy(row => row.CommunicationStatusText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "area" => rows.OrderBy(row => row.AreaType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            "health" => rows.OrderBy(row => row.Health.NeedsReview ? 0 : 1)
                .ThenBy(row => row.Health.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase),
            "indoor" => rows.OrderBy(TemperatureValue)
                .ThenBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(row => row.Building, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Floor ?? double.MaxValue)
                .ThenBy(row => row.SubArea, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.PageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
        };

        return query.SortDescending ? sorted.Reverse() : sorted;
    }

    private static async Task<IReadOnlyList<DeviceFilterOption>> LoadOptionsAsync(
        SqliteConnection connection,
        string sql,
        IReadOnlyDictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        var rows = new List<DeviceFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DeviceFilterOption(
                Value: SqliteValueReader.ReadString(reader, "value"),
                Label: SqliteValueReader.ReadString(reader, "label"),
                Count: Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("count")), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static IReadOnlyList<string> LoadBuildingsForRealtimeOptions(IReadOnlyList<DeviceFilterOption> buildings)
    {
        return buildings
            .Select(option => option.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<DeviceFilterOption> CountOptions<T>(
        IEnumerable<T> rows,
        Func<T, string?> selector,
        bool sortByCountDescending = false,
        Func<DeviceFilterOption, double>? sortKey = null)
    {
        var options = rows
            .Select(selector)
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DeviceFilterOption(group.Key, group.Key, group.Count()))
            .ToList();

        if (sortKey is not null)
        {
            return options
                .OrderBy(sortKey)
                .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return sortByCountDescending
            ? options
                .OrderByDescending(option => option.Count)
                .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : options
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private static IReadOnlyList<DeviceFilterOption> PageOptions(IEnumerable<DeviceRecord> rows)
    {
        return CountOptions(rows, row => string.IsNullOrWhiteSpace(row.PageName) ? "default" : row.PageName)
            .Select(option => option with { Label = DevicePageNameFormatter.Format(option.Value) })
            .OrderBy(option => DevicePageNameFormatter.SortValue(option.Value))
            .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ShouldExposeZuo(DeviceRecord row)
    {
        return row.Building is "5号" or "6号" && !string.IsNullOrWhiteSpace(row.Zuo);
    }

    private static IReadOnlyList<DeviceFilterOption> RealtimeOptions(
        IEnumerable<RealtimeDetailRecord> rows,
        Func<RealtimeDetailRecord, string> selector)
    {
        return rows
            .Select(selector)
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DeviceFilterOption(group.Key, group.Key, group.Count()))
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<DeviceFilterOption>> LoadFloorOptionsAsync(
        SqliteConnection connection,
        DeviceSqlSource source,
        string whereSql,
        IReadOnlyDictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.floor, s.text AS sub_area, COUNT(*) AS count
            {{FROM_SQL}}
            {{WHERE_SQL}}
            GROUP BY s.id
            """
            .Replace("{{FROM_SQL}}", source.FromSql, StringComparison.Ordinal)
            .Replace("{{WHERE_SQL}}", whereSql, StringComparison.Ordinal);
        AddParameters(command, parameters);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var label = DeviceFloorLabelFormatter.Format(
                SqliteValueReader.ReadNullableDouble(reader, "floor"),
                SqliteValueReader.ReadString(reader, "sub_area"));
            counts[label] = counts.GetValueOrDefault(label) +
                            Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("count")), CultureInfo.InvariantCulture);
        }

        return counts
            .OrderBy(item => FloorSortValue(item.Key))
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DeviceFilterOption(item.Key, item.Key, item.Value))
            .ToList();
    }

    private static Task<IReadOnlyList<DeviceFilterOption>> LoadTextOptionsAsync(
        SqliteConnection connection,
        DeviceSqlSource source,
        string whereSql,
        IReadOnlyDictionary<string, object> parameters,
        string expression,
        CancellationToken cancellationToken)
    {
        var valueExpression = $"IFNULL(NULLIF(TRIM({expression}), ''), '')";
        return LoadOptionsAsync(
            connection,
            $"""
            SELECT {valueExpression} AS value, {valueExpression} AS label, COUNT(*) AS count
            {source.FromSql}
            {whereSql}
            GROUP BY {valueExpression}
            HAVING {valueExpression} <> ''
            ORDER BY {valueExpression}
            """,
            parameters,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<DeviceFilterOption>> LoadZuoOptionsAsync(
        SqliteConnection connection,
        DeviceSqlSource source,
        string whereSql,
        IReadOnlyDictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.building, s.x, COUNT(*) AS count
            {{FROM_SQL}}
            {{WHERE_SQL}}
            GROUP BY s.id
            """
            .Replace("{{FROM_SQL}}", source.FromSql, StringComparison.Ordinal)
            .Replace("{{WHERE_SQL}}", whereSql, StringComparison.Ordinal);
        AddParameters(command, parameters);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var zuo = DeviceZuoClassifier.Classify(
                SqliteValueReader.ReadString(reader, "building"),
                SqliteValueReader.ReadNullableDouble(reader, "x"));
            if (string.IsNullOrWhiteSpace(zuo))
            {
                continue;
            }

            counts[zuo] = counts.GetValueOrDefault(zuo) +
                          Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("count")), CultureInfo.InvariantCulture);
        }

        return counts
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new DeviceFilterOption(item.Key, item.Key, item.Value))
            .ToList();
    }

    private static async Task<IReadOnlyList<DeviceFilterOption>> LoadTagOptionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaGuard.TableExistsAsync(connection, "device_tags", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag AS value, tag AS label, COUNT(*) AS count
            FROM device_tags
            GROUP BY tag
            ORDER BY tag
            """;
        return await LoadOptionsFromCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<DeviceFilterOption>> LoadOptionsFromCommandAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<DeviceFilterOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DeviceFilterOption(
                Value: SqliteValueReader.ReadString(reader, "value"),
                Label: SqliteValueReader.ReadString(reader, "label"),
                Count: Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("count")), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private static async Task<AnnotationMaps> LoadAnnotationMapsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (await SqliteSchemaGuard.TableExistsAsync(connection, "device_notes", cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT building, card_name, note FROM device_notes";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                notes[AnnotationKey(SqliteValueReader.ReadString(reader, "building"), SqliteValueReader.ReadString(reader, "card_name"))] =
                    SqliteValueReader.ReadString(reader, "note");
            }
        }

        if (await SqliteSchemaGuard.TableExistsAsync(connection, "device_tags", cancellationToken).ConfigureAwait(false))
        {
            var tagLists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT building, card_name, tag FROM device_tags ORDER BY tag";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tag = SqliteValueReader.ReadString(reader, "tag");
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                var key = AnnotationKey(SqliteValueReader.ReadString(reader, "building"), SqliteValueReader.ReadString(reader, "card_name"));
                if (!tagLists.TryGetValue(key, out var list))
                {
                    list = [];
                    tagLists[key] = list;
                }

                if (!list.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(tag);
                }
            }

            foreach (var item in tagLists)
            {
                tags[item.Key] = item.Value;
            }
        }

        return new AnnotationMaps(notes, tags);
    }

    private static async Task<RealtimeMatchOverrideSet> LoadRealtimeMatchOverridesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaGuard.TableExistsAsync(connection, "realtime_match_overrides", cancellationToken).ConfigureAwait(false))
        {
            return RealtimeMatchOverrideSet.Empty;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, building, dev_id, floor_label, sub_area, page_name, realtime_name,
                   action, target_card_id, zuo_override, area_type_override, note
            FROM realtime_match_overrides
            ORDER BY id
            """;
        var rows = new List<RealtimeMatchOverride>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RealtimeMatchOverride(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: SqliteValueReader.ReadString(reader, "building"),
                DevId: SqliteValueReader.ReadString(reader, "dev_id"),
                FloorLabel: SqliteValueReader.ReadString(reader, "floor_label"),
                SubArea: SqliteValueReader.ReadString(reader, "sub_area"),
                PageName: NormalizePageName(SqliteValueReader.ReadString(reader, "page_name")),
                RealtimeName: SqliteValueReader.ReadString(reader, "realtime_name"),
                Action: NormalizeOverrideAction(SqliteValueReader.ReadString(reader, "action")),
                TargetCardId: SqliteValueReader.ReadNullableInt64(reader, "target_card_id"),
                ZuoOverride: SqliteValueReader.ReadString(reader, "zuo_override"),
                AreaTypeOverride: NormalizeAreaTypeOverride(SqliteValueReader.ReadString(reader, "area_type_override")),
                Note: SqliteValueReader.ReadString(reader, "note")));
        }

        return new RealtimeMatchOverrideSet(rows);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var (key, value) in parameters)
        {
            command.Parameters.AddWithValue(key, value);
        }
    }

    private static IReadOnlyList<string> ResolveRealtimeBuildings(DeviceQuery query, IReadOnlyList<DeviceRecord> rows)
    {
        if (!string.IsNullOrWhiteSpace(query.Building))
        {
            return [query.Building.Trim()];
        }

        return rows
            .Select(row => row.Building)
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DeviceRecord> AttachRealtimeRows(
        List<DeviceRecord> rows,
        RealtimeDetailSet realtimeSet,
        RealtimeMatchOverrideSet overrides)
    {
        if (realtimeSet.Rows.Count == 0)
        {
            return rows;
        }

        var byId = rows.ToDictionary(row => row.Id);
        var byExact = BuildDeviceIndex(rows, row => RealtimeKeyBuilder.ExactKey(
            row.Building,
            row.Floor,
            row.SubArea,
            row.PageName,
            row.Name));
        var byName = BuildDeviceIndex(rows, row => RealtimeKeyBuilder.NameKey(row.Building, row.Name));
        var manualMatches = new Dictionary<long, ManualRealtimeMatch>();
        var virtualRows = new List<DeviceRecord>();

        foreach (var detail in realtimeSet.Rows)
        {
            var matchOverride = overrides.Find(detail);
            if (matchOverride is null)
            {
                continue;
            }

            if (matchOverride.IsIgnoredDuplicate)
            {
                realtimeSet.MarkUsed(detail);
                continue;
            }

            if (matchOverride.IsVirtual)
            {
                realtimeSet.MarkUsed(detail);
                virtualRows.Add(CreateVirtualRecord(detail, matchOverride));
                continue;
            }

            if (!matchOverride.IsMapToDatabase)
            {
                continue;
            }

            var target = ResolveOverrideTarget(matchOverride, detail, byId, byExact, byName);
            if (target is null)
            {
                continue;
            }

            realtimeSet.MarkUsed(detail);
            manualMatches[target.Id] = new ManualRealtimeMatch(detail, matchOverride);
        }

        var attached = new List<DeviceRecord>(rows.Count + virtualRows.Count);
        foreach (var row in rows)
        {
            if (manualMatches.TryGetValue(row.Id, out var manual))
            {
                attached.Add(ApplyRealtime(row, manual.Detail, "manual", manual.Override));
                continue;
            }

            var exact = realtimeSet.TakeExact(RealtimeKeyBuilder.ExactKey(
                row.Building,
                row.Floor,
                row.SubArea,
                row.PageName,
                row.Name));
            if (exact is not null)
            {
                var matchOverride = overrides.Find(exact);
                attached.Add(ApplyRealtime(row, exact, AutomaticMatchKind(matchOverride, "exact"), matchOverride));
                continue;
            }

            var byNameDetail = realtimeSet.UniqueByName(RealtimeKeyBuilder.NameKey(row.Building, row.Name));
            if (byNameDetail is not null)
            {
                var matchOverride = overrides.Find(byNameDetail);
                attached.Add(ApplyRealtime(row, byNameDetail, AutomaticMatchKind(matchOverride, "name"), matchOverride));
                continue;
            }

            attached.Add(row);
        }

        attached.AddRange(virtualRows);
        return attached;
    }

    private static DeviceRecord ApplyRealtime(
        DeviceRecord row,
        RealtimeDetailRecord detail,
        string matchKind,
        RealtimeMatchOverride? matchOverride)
    {
        return row with
        {
            Realtime = detail,
            RealtimeMatchKind = matchKind,
            AreaTypeOverride = string.IsNullOrWhiteSpace(matchOverride?.AreaTypeOverride)
                ? row.AreaTypeOverride
                : matchOverride.AreaTypeOverride,
            Zuo = string.IsNullOrWhiteSpace(matchOverride?.ZuoOverride) ? row.Zuo : matchOverride.ZuoOverride,
            ZuoSource = string.IsNullOrWhiteSpace(matchOverride?.ZuoOverride) ? row.ZuoSource : "manual",
            MatchOverrideId = matchOverride?.Id,
            MatchOverrideAction = matchOverride?.Action,
            MatchOverrideNote = matchOverride?.Note
        };
    }

    private static string AutomaticMatchKind(RealtimeMatchOverride? matchOverride, string fallback)
    {
        if (matchOverride is null)
        {
            return fallback;
        }

        return matchOverride.IsMapToDatabase
            ? "manual"
            : "classify";
    }

    private static DeviceRecord? ResolveOverrideTarget(
        RealtimeMatchOverride matchOverride,
        RealtimeDetailRecord detail,
        IReadOnlyDictionary<long, DeviceRecord> byId,
        IReadOnlyDictionary<string, List<DeviceRecord>> byExact,
        IReadOnlyDictionary<string, List<DeviceRecord>> byName)
    {
        if (matchOverride.TargetCardId is not null &&
            byId.TryGetValue(matchOverride.TargetCardId.Value, out var byTargetId))
        {
            return byTargetId;
        }

        return UniqueFromIndex(byExact, RealtimeKeyBuilder.ExactKey(detail)) ??
               UniqueFromIndex(byName, RealtimeKeyBuilder.NameKey(detail.Building, detail.Name));
    }

    private static DeviceRecord CreateVirtualRecord(RealtimeDetailRecord detail, RealtimeMatchOverride matchOverride)
    {
        var communicationText = CommunicationFromRealtime(detail);
        return new DeviceRecord(
            Id: -Math.Abs(matchOverride.Id),
            Building: detail.Building,
            Floor: detail.Floor,
            FloorLabel: DeviceFloorLabelFormatter.Format(detail.Floor, detail.SubArea),
            SubArea: detail.SubArea,
            X: null,
            Y: null,
            PageName: NormalizePageName(detail.PageName),
            Name: detail.Name,
            Layout: string.Empty,
            SwitchState: PowerToSwitch(detail.PowerState, detail.CardSwitch),
            Mode: detail.Mode,
            IndoorTemperature: detail.IndoorTemperature,
            SetTemperature: detail.SetTemperature,
            Fan: detail.Fan,
            Indicator: detail.CardIndicator,
            CommunicationText: communicationText,
            CommunicationState: DeviceCommunicationStateParser.Parse(communicationText),
            Realtime: detail,
            RealtimeMatchKind: "virtual",
            AreaTypeOverride: string.IsNullOrWhiteSpace(matchOverride.AreaTypeOverride)
                ? null
                : matchOverride.AreaTypeOverride,
            Zuo: string.IsNullOrWhiteSpace(matchOverride.ZuoOverride) ? null : matchOverride.ZuoOverride,
            ZuoSource: string.IsNullOrWhiteSpace(matchOverride.ZuoOverride) ? null : "manual",
            Note: string.Empty,
            Tags: [],
            MatchOverrideId: matchOverride.Id,
            MatchOverrideAction: matchOverride.Action,
            MatchOverrideNote: matchOverride.Note,
            IsVirtual: true);
    }

    private static string CommunicationFromRealtime(RealtimeDetailRecord detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.CardComm))
        {
            return detail.CardComm;
        }

        if (detail.PowerState is "开机" or "关机")
        {
            return detail.PowerState;
        }

        return detail.CardSwitch switch
        {
            "ON" => "开机",
            "OFF" => "关机",
            _ => string.Empty,
        };
    }

    private static string PowerToSwitch(string powerState, string cardSwitch)
    {
        return powerState switch
        {
            "开机" => "ON",
            "关机" => "OFF",
            _ => cardSwitch,
        };
    }

    private static Dictionary<string, List<DeviceRecord>> BuildDeviceIndex(
        IEnumerable<DeviceRecord> rows,
        Func<DeviceRecord, string> keySelector)
    {
        var index = new Dictionary<string, List<DeviceRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = keySelector(row);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(row);
        }

        return index;
    }

    private static DeviceRecord? UniqueFromIndex(
        IReadOnlyDictionary<string, List<DeviceRecord>> index,
        string key)
    {
        return index.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
    }

    private static string AnnotationKey(string building, string name)
    {
        return $"{(building ?? string.Empty).Trim().ToUpperInvariant()}::{(name ?? string.Empty).Trim().ToUpperInvariant()}";
    }

    private static string NormalizePageName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
    }

    private static string NormalizeOverrideAction(string value)
    {
        return value.Trim() switch
        {
            "map_to_db" => "map_to_db",
            "create_virtual" => "create_virtual",
            "ignore_duplicate" => "ignore_duplicate",
            "classify_only" => "classify_only",
            _ => "classify_only",
        };
    }

    private static string NormalizeAreaTypeOverride(string value)
    {
        return value.Trim() switch
        {
            "公区" => "公区",
            "非公区" => "非公区",
            "未匹配" => "未匹配",
            _ => string.Empty,
        };
    }

    private static double FloorSortValue(string floorLabel)
    {
        var normalized = DeviceFloorLabelFormatter.Normalize(floorLabel);
        if (normalized == "BM")
        {
            return -0.5;
        }

        if (normalized.StartsWith('B') &&
            double.TryParse(
                normalized[1..].TrimEnd('F'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var basement))
        {
            return -basement;
        }

        return double.TryParse(
            normalized.TrimEnd('F'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var floor)
            ? floor
            : double.MaxValue;
    }

    private sealed record AnnotationMaps(
        IReadOnlyDictionary<string, string> Notes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Tags);

    private sealed record ManualRealtimeMatch(
        RealtimeDetailRecord Detail,
        RealtimeMatchOverride Override);

    private sealed record DeviceSqlSource(
        string FromSql,
        long? RunId)
    {
        public bool IsHistory => RunId is not null;

        public static DeviceSqlSource For(long? runId)
        {
            return runId is null
                ? new DeviceSqlSource(
                    """
                    FROM cards c
                    JOIN pages p ON c.page_id = p.id
                    JOIN sub_areas s ON p.sub_area_id = s.id
                    """,
                    null)
                : new DeviceSqlSource(
                    """
                    FROM run_cards c
                    JOIN run_pages p ON c.run_page_id = p.id
                    JOIN run_sub_areas s ON p.run_sub_area_id = s.id
                    """,
                    runId);
        }
    }

    private static AnnotationMaps EmptyAnnotations()
    {
        return new AnnotationMaps(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
    }
}
