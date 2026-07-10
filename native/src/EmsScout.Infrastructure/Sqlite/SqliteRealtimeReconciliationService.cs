using EmsScout.Application.Devices;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteRealtimeReconciliationService(
    string databasePath,
    IRealtimeDetailSource realtimeDetailSource) : IRealtimeReconciliationService
{
    public SqliteRealtimeReconciliationService(
        Func<string> databasePathResolver,
        IRealtimeDetailSource realtimeDetailSource)
        : this(string.Empty, realtimeDetailSource)
    {
        DatabasePathResolver = databasePathResolver;
    }

    private Func<string> DatabasePathResolver { get; } = () => databasePath;

    private static readonly string[] BuildingOrder = ["1号", "2号", "3号", "4号", "5号", "6号"];

    public async Task<RealtimeReconciliationResult> AnalyzeAsync(
        RealtimeReconciliationQuery query,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabaseExists();

        await using var connection = OpenConnection();
        var buildings = ResolveBuildings(query.Building);
        var dbRows = await LoadDbRowsAsync(connection, buildings, cancellationToken).ConfigureAwait(false);
        var realtimeSet = await realtimeDetailSource.LoadAsync(buildings, cancellationToken).ConfigureAwait(false);
        var realtimeRows = realtimeSet.Rows.ToList();
        var overrides = await LoadRealtimeMatchOverridesAsync(connection, cancellationToken).ConfigureAwait(false);

        var state = new ReconcileState(dbRows, realtimeRows, overrides);
        ConsumeExactMatches(state);
        ApplyRealtimeOverrides(state);
        ConsumeRelaxedMatches(state);
        AddUnmatchedRealtimeItems(state);
        AddMissingDbItems(state);

        var allItems = state.Items
            .OrderBy(item => TypeSort(item.Type))
            .ThenBy(item => item.Building, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FloorLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var filtered = allItems
            .Where(item => MatchesType(item, query.DiffType))
            .Where(item => MatchesSearch(item, query.SearchText))
            .ToList();
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 2000);
        var summary = new RealtimeReconciliationSummary(
            DbCount: dbRows.Count,
            RealtimeCount: realtimeRows.Count,
            Difference: realtimeRows.Count - dbRows.Count,
            DiffItemCount: allItems.Count,
            ExactMatches: state.ExactMatches,
            ManualMatches: state.ManualMatches,
            RelaxedMatches: state.RelaxedMatches,
            OverrideCount: overrides.Rows.Count,
            ByType: allItems
                .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            GeneratedAt: DateTimeOffset.Now,
            SourceUpdatedAt: realtimeRows.Count == 0
                ? null
                : realtimeRows.Max(row => row.SourceUpdatedAt));
        return new RealtimeReconciliationResult(
            summary,
            filtered.Skip(offset).Take(limit).ToList());
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePathResolver()};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        return connection;
    }

    private void EnsureDatabaseExists()
    {
        var databasePath = DatabasePathResolver();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Cannot find EMS SQLite database.", databasePath);
        }
    }

    private static async Task<IReadOnlyList<DbReconcileRow>> LoadDbRowsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> buildings,
        CancellationToken cancellationToken)
    {
        var where = buildings.Count > 0
            ? $"WHERE s.building IN ({string.Join(",", buildings.Select((_, index) => "$building" + index))})"
            : string.Empty;
        var sql = $"""
            SELECT
              c.id,
              s.building,
              s.floor,
              s.text AS sub_area,
              p.page_name,
              c.name,
              c.switch,
              c.comm
            FROM cards c
            JOIN pages p ON c.page_id = p.id
            JOIN sub_areas s ON p.sub_area_id = s.id
            {where}
            ORDER BY s.building, s.floor, s.x, p.id, c.name
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < buildings.Count; i++)
        {
            command.Parameters.AddWithValue("$building" + i, buildings[i]);
        }

        var rows = new List<DbReconcileRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var floor = ReadNullableDouble(reader, "floor");
            var subArea = ReadString(reader, "sub_area");
            rows.Add(new DbReconcileRow(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                Building: ReadString(reader, "building"),
                Floor: floor,
                FloorLabel: DeviceFloorLabelFormatter.Format(floor, subArea),
                SubArea: subArea,
                PageName: NormalizePageName(ReadString(reader, "page_name")),
                Name: ReadString(reader, "name"),
                SwitchState: ReadString(reader, "switch"),
                CommunicationText: ReadString(reader, "comm")));
        }

        return rows;
    }

    private static async Task<RealtimeMatchOverrideSet> LoadRealtimeMatchOverridesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "realtime_match_overrides", cancellationToken).ConfigureAwait(false))
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
                Building: ReadString(reader, "building"),
                DevId: ReadString(reader, "dev_id"),
                FloorLabel: ReadString(reader, "floor_label"),
                SubArea: ReadString(reader, "sub_area"),
                PageName: NormalizePageName(ReadString(reader, "page_name")),
                RealtimeName: ReadString(reader, "realtime_name"),
                Action: NormalizeOverrideAction(ReadString(reader, "action")),
                TargetCardId: ReadNullableInt64(reader, "target_card_id"),
                ZuoOverride: ReadString(reader, "zuo_override"),
                AreaTypeOverride: ReadString(reader, "area_type_override"),
                Note: ReadString(reader, "note")));
        }

        return new RealtimeMatchOverrideSet(rows);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static void ConsumeExactMatches(ReconcileState state)
    {
        var available = BuildRealtimeIndex(state.RealtimeRows, ExactKey);
        foreach (var dbRow in state.DbRows)
        {
            var realtime = TakeAvailable(available, ExactKey(dbRow), state.RealtimeConsumed);
            if (realtime is null)
            {
                continue;
            }

            state.DbConsumed.Add(dbRow.Id);
            state.RealtimeConsumed.Add(realtime.RowId);
            state.ExactMatches++;
        }
    }

    private static void ApplyRealtimeOverrides(ReconcileState state)
    {
        foreach (var realtime in state.RealtimeRows)
        {
            var matchOverride = state.Overrides.Find(realtime);
            if (matchOverride is null)
            {
                continue;
            }

            if (matchOverride.IsVirtual)
            {
                state.RealtimeConsumed.Add(realtime.RowId);
                state.Items.Add(Item(
                    RealtimeReconciliationTypes.VirtualOverride,
                    "Info",
                    ConfidenceFor(RealtimeReconciliationTypes.VirtualOverride, realtime, null, matchOverride),
                    null,
                    realtime,
                    matchOverride,
                    "人工覆盖为虚拟纳管，实时详情存在但当前 SQLite 基线无实体卡片。"));
                continue;
            }

            if (matchOverride.IsIgnoredDuplicate)
            {
                state.RealtimeConsumed.Add(realtime.RowId);
                state.Items.Add(Item(
                    RealtimeReconciliationTypes.DuplicateRender,
                    "Info",
                    ConfidenceFor(RealtimeReconciliationTypes.DuplicateRender, realtime, null, matchOverride),
                    null,
                    realtime,
                    matchOverride,
                    "人工覆盖为忽略重复实时行。"));
                continue;
            }

            if (!matchOverride.IsMapToDatabase)
            {
                continue;
            }

            var dbRow = ResolveOverrideTarget(matchOverride, realtime, state);
            if (dbRow is not null)
            {
                state.RealtimeConsumed.Add(realtime.RowId);
                state.DbConsumed.Add(dbRow.Id);
                state.ManualMatches++;
            }

            state.Items.Add(Item(
                RealtimeReconciliationTypes.MatchFailed,
                dbRow is null ? "Warning" : "Info",
                ConfidenceFor(RealtimeReconciliationTypes.MatchFailed, realtime, dbRow, matchOverride),
                dbRow,
                realtime,
                matchOverride,
                dbRow is null
                    ? "人工映射未解析到目标 DB 卡片，需要复核覆盖记录。"
                    : "实时详情与 DB 精确位置不一致，由人工覆盖映射到 DB 卡片。"));
        }
    }

    private static void ConsumeRelaxedMatches(ReconcileState state)
    {
        var available = BuildRealtimeIndex(state.RealtimeRows, NameFloorKey);
        foreach (var dbRow in state.DbRows)
        {
            if (state.DbConsumed.Contains(dbRow.Id))
            {
                continue;
            }

            var realtime = TakeAvailable(available, NameFloorKey(dbRow), state.RealtimeConsumed);
            if (realtime is null)
            {
                continue;
            }

            state.DbConsumed.Add(dbRow.Id);
            state.RealtimeConsumed.Add(realtime.RowId);
            state.RelaxedMatches++;
        }
    }

    private static void AddUnmatchedRealtimeItems(ReconcileState state)
    {
        var dbByNameFloor = BuildDbIndex(state.DbRows, NameFloorKey);
        var dbByExact = BuildDbIndex(state.DbRows, ExactKey);
        foreach (var realtime in state.RealtimeRows)
        {
            if (state.RealtimeConsumed.Contains(realtime.RowId))
            {
                continue;
            }

            var dbRow = FirstFromIndex(dbByNameFloor, NameFloorKey(realtime)) ??
                        FirstFromIndex(dbByExact, ExactKey(realtime));
            var type = ClassifyUnmatchedRealtime(realtime, dbRow, state);
            var matchOverride = state.Overrides.Find(realtime);
            state.Items.Add(Item(
                type,
                SeverityFor(type),
                ConfidenceFor(type, realtime, dbRow, matchOverride),
                dbRow,
                realtime,
                matchOverride,
                ReasonFor(type, dbRow)));
        }
    }

    private static void AddMissingDbItems(ReconcileState state)
    {
        foreach (var dbRow in state.DbRows)
        {
            if (state.DbConsumed.Contains(dbRow.Id))
            {
                continue;
            }

            state.Items.Add(Item(
                RealtimeReconciliationTypes.MissingInRealtime,
                "Warning",
                ConfidenceFor(RealtimeReconciliationTypes.MissingInRealtime, null, dbRow, null),
                dbRow,
                null,
                null,
                "DB 中存在该卡片，但最新实时详情文件没有可匹配设备行。"));
        }
    }

    private static DbReconcileRow? ResolveOverrideTarget(
        RealtimeMatchOverride matchOverride,
        RealtimeDetailRecord realtime,
        ReconcileState state)
    {
        if (matchOverride.TargetCardId is not null &&
            state.DbById.TryGetValue(matchOverride.TargetCardId.Value, out var byId))
        {
            return byId;
        }

        var byExact = UniqueFromIndex(state.DbByExact, ExactKey(realtime));
        if (byExact is not null)
        {
            return byExact;
        }

        return UniqueFromIndex(state.DbByNameFloor, NameFloorKey(realtime));
    }

    private static string ClassifyUnmatchedRealtime(
        RealtimeDetailRecord realtime,
        DbReconcileRow? dbRow,
        ReconcileState state)
    {
        if (IsRealtimeNoisy(realtime))
        {
            return RealtimeReconciliationTypes.DataNoise;
        }

        if (IsDuplicateRender(realtime, state))
        {
            return RealtimeReconciliationTypes.DuplicateRender;
        }

        return dbRow is null
            ? RealtimeReconciliationTypes.NewDevice
            : RealtimeReconciliationTypes.MatchFailed;
    }

    private static bool IsDuplicateRender(RealtimeDetailRecord realtime, ReconcileState state)
    {
        var exactKey = ExactKey(realtime);
        if (!string.IsNullOrWhiteSpace(exactKey) &&
            state.RealtimeRows.Count(row => ExactKey(row).Equals(exactKey, StringComparison.OrdinalIgnoreCase)) > 1)
        {
            return true;
        }

        var nameFloorKey = NameFloorKey(realtime);
        if (string.IsNullOrWhiteSpace(nameFloorKey))
        {
            return false;
        }

        var realtimeSameNameFloor = state.RealtimeRows.Count(row =>
            NameFloorKey(row).Equals(nameFloorKey, StringComparison.OrdinalIgnoreCase));
        var dbSameNameFloor = state.DbRows.Count(row =>
            NameFloorKey(row).Equals(nameFloorKey, StringComparison.OrdinalIgnoreCase));
        return realtimeSameNameFloor > Math.Max(1, dbSameNameFloor);
    }

    private static bool IsRealtimeNoisy(RealtimeDetailRecord realtime)
    {
        if (realtime.IsInvalid)
        {
            return true;
        }

        if (realtime.FieldCount is > 0 and < 20)
        {
            return true;
        }

        return realtime.RealtimeTagCount > 0 &&
               realtime.RealtimeValidTagCount == 0 &&
               realtime.CardComm != "离线";
    }

    private static RealtimeReconciliationItem Item(
        string type,
        string severity,
        double confidence,
        DbReconcileRow? dbRow,
        RealtimeDetailRecord? realtime,
        RealtimeMatchOverride? matchOverride,
        string reason)
    {
        var building = realtime?.Building ?? dbRow?.Building ?? string.Empty;
        var floor = realtime is not null
            ? DeviceFloorLabelFormatter.Format(realtime.Floor, realtime.SubArea)
            : dbRow?.FloorLabel ?? string.Empty;
        var name = realtime?.Name ?? dbRow?.Name ?? string.Empty;
        return new RealtimeReconciliationItem(
            Type: type,
            Severity: severity,
            Building: building,
            FloorLabel: floor,
            Name: name,
            DbLocation: dbRow is null ? "--" : $"{dbRow.SubArea} / {dbRow.PageName}",
            RealtimeLocation: realtime is null ? "--" : $"{realtime.SubArea} / {realtime.PageName}",
            DevId: realtime?.DevId ?? string.Empty,
            OverrideAction: matchOverride?.Action ?? string.Empty,
            Reason: reason,
            Confidence: confidence,
            RuleVersion: RealtimeReconciliationTypes.RuleVersion,
            RuleDescription: RuleDescriptionFor(type),
            EvidenceSummary: EvidenceSummary(dbRow, realtime, matchOverride),
            DecisionPath: DecisionPath(type, dbRow, realtime, matchOverride));
    }

    private static double ConfidenceFor(
        string type,
        RealtimeDetailRecord? realtime,
        DbReconcileRow? dbRow,
        RealtimeMatchOverride? matchOverride)
    {
        if (matchOverride is not null)
        {
            return 0.95;
        }

        return type switch
        {
            RealtimeReconciliationTypes.DataNoise => realtime?.Error.Length > 0 || realtime?.DefaultLike == true ? 0.95 : 0.7,
            RealtimeReconciliationTypes.DuplicateRender => 0.95,
            RealtimeReconciliationTypes.MatchFailed => dbRow is null ? 0.7 : 0.95,
            RealtimeReconciliationTypes.NewDevice => IsRealtimeNoisy(realtime!) ? 0.35 : 0.95,
            RealtimeReconciliationTypes.MissingInRealtime => 0.95,
            _ => 0.5,
        };
    }

    private static string RuleDescriptionFor(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => "实时行未被精确匹配、未命中人工覆盖、未找到同楼栋同楼层同名 DB 卡，且不是噪声或重复渲染。",
            RealtimeReconciliationTypes.MissingInRealtime => "DB 卡片未被精确匹配、人工覆盖或宽松同名同楼层匹配消费。",
            RealtimeReconciliationTypes.MatchFailed => "DB 与实时可通过人工覆盖或同名同楼层关联，但精确身份键不一致。",
            RealtimeReconciliationTypes.DuplicateRender => "实时详情出现同名同页重复行，或实时同楼层同名数量超过 DB 唯一卡片数量。",
            RealtimeReconciliationTypes.DataNoise => "实时行存在采集 error、默认模板、字段数量异常或非离线设备有效点位为 0。",
            RealtimeReconciliationTypes.VirtualOverride => "realtime_match_overrides.action=create_virtual 明确声明该实时设备虚拟纳管。",
            _ => "未命中任何已知归因规则。",
        };
    }

    private static string EvidenceSummary(
        DbReconcileRow? dbRow,
        RealtimeDetailRecord? realtime,
        RealtimeMatchOverride? matchOverride)
    {
        var values = new List<string>();
        if (dbRow is not null)
        {
            values.Add($"DB exact={ExactKey(dbRow)}");
            values.Add($"DB nameFloor={NameFloorKey(dbRow)}");
        }

        if (realtime is not null)
        {
            values.Add($"RT exact={ExactKey(realtime)}");
            values.Add($"RT nameFloor={NameFloorKey(realtime)}");
            values.Add($"points={realtime.RealtimeValidTagCount}/{realtime.RealtimeTagCount}");
            if (realtime.DefaultLike)
            {
                values.Add("defaultLike=true");
            }

            if (!string.IsNullOrWhiteSpace(realtime.Error))
            {
                values.Add($"error={realtime.Error}");
            }
        }

        if (matchOverride is not null)
        {
            values.Add($"override=#{matchOverride.Id}:{matchOverride.Action}");
            if (matchOverride.TargetCardId is not null)
            {
                values.Add($"target={matchOverride.TargetCardId}");
            }
        }

        return values.Count == 0 ? "--" : string.Join("; ", values);
    }

    private static IReadOnlyList<string> DecisionPath(
        string type,
        DbReconcileRow? dbRow,
        RealtimeDetailRecord? realtime,
        RealtimeMatchOverride? matchOverride)
    {
        var steps = new List<string>
        {
            $"规则版本 {RealtimeReconciliationTypes.RuleVersion}",
        };

        if (dbRow is not null)
        {
            steps.Add("读取 SQLite cards 基线并生成 DB 身份键");
        }

        if (realtime is not null)
        {
            steps.Add("读取 realtime latest JSON 并生成实时身份键");
        }

        steps.Add("匹配顺序：精确身份键 -> 人工覆盖 -> 同楼层同名宽松匹配 -> 差异归因");
        if (matchOverride is not null)
        {
            steps.Add($"命中人工覆盖 realtime_match_overrides#{matchOverride.Id} action={matchOverride.Action}");
        }
        else
        {
            steps.Add("未命中人工覆盖，进入自动规则判断");
        }

        if (dbRow is not null && realtime is not null)
        {
            var exactSame = ExactKey(dbRow).Equals(ExactKey(realtime), StringComparison.OrdinalIgnoreCase);
            var nameFloorSame = NameFloorKey(dbRow).Equals(NameFloorKey(realtime), StringComparison.OrdinalIgnoreCase);
            steps.Add(exactSame ? "DB 与实时 exact identity 一致" : "DB 与实时 exact identity 不一致");
            steps.Add(nameFloorSame ? "楼栋 + 楼层 + 名称一致，允许降级解释" : "楼栋 + 楼层 + 名称未完全一致");
        }
        else if (realtime is not null)
        {
            steps.Add("实时设备未找到可消费的 DB 卡片");
        }
        else if (dbRow is not null)
        {
            steps.Add("DB 卡片未找到可消费的实时详情行");
        }

        if (realtime is not null && IsRealtimeNoisy(realtime))
        {
            steps.Add("实时详情存在噪声证据：error/defaultLike/有效点位异常");
        }

        steps.Add($"归因结果：{type}");
        return steps;
    }

    private static string ReasonFor(string type, DbReconcileRow? dbRow)
    {
        return type switch
        {
            RealtimeReconciliationTypes.DataNoise => "实时详情行存在错误、默认值或点位不完整，先按数据噪声处理。",
            RealtimeReconciliationTypes.DuplicateRender => "实时详情存在重复 DOM/重复采集行，DB 已按唯一卡片保留。",
            RealtimeReconciliationTypes.MatchFailed => dbRow is null
                ? "实时详情未能与 DB 精确匹配，需要人工确认位置。"
                : "DB 存在同楼层同名卡片，但实时详情位置与 DB 不一致。",
            RealtimeReconciliationTypes.NewDevice => "DB 中未找到同楼栋同楼层同名卡片，实时详情存在有效设备行。",
            _ => "需要复核的实时源差异。",
        };
    }

    private static string SeverityFor(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => "Warning",
            RealtimeReconciliationTypes.MissingInRealtime => "Warning",
            RealtimeReconciliationTypes.MatchFailed => "Warning",
            RealtimeReconciliationTypes.DataNoise => "Info",
            RealtimeReconciliationTypes.DuplicateRender => "Info",
            RealtimeReconciliationTypes.VirtualOverride => "Info",
            _ => "Info",
        };
    }

    private static Dictionary<string, List<RealtimeDetailRecord>> BuildRealtimeIndex(
        IEnumerable<RealtimeDetailRecord> rows,
        Func<RealtimeDetailRecord, string> keySelector)
    {
        var index = new Dictionary<string, List<RealtimeDetailRecord>>(StringComparer.OrdinalIgnoreCase);
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

    private static Dictionary<string, List<DbReconcileRow>> BuildDbIndex(
        IEnumerable<DbReconcileRow> rows,
        Func<DbReconcileRow, string> keySelector)
    {
        var index = new Dictionary<string, List<DbReconcileRow>>(StringComparer.OrdinalIgnoreCase);
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

    private static RealtimeDetailRecord? TakeAvailable(
        IReadOnlyDictionary<string, List<RealtimeDetailRecord>> index,
        string key,
        ISet<string> consumed)
    {
        return index.TryGetValue(key, out var values)
            ? values.FirstOrDefault(row => !consumed.Contains(row.RowId))
            : null;
    }

    private static DbReconcileRow? FirstFromIndex(
        IReadOnlyDictionary<string, List<DbReconcileRow>> index,
        string key)
    {
        return index.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
    }

    private static DbReconcileRow? UniqueFromIndex(
        IReadOnlyDictionary<string, List<DbReconcileRow>> index,
        string key)
    {
        return index.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
    }

    private static string ExactKey(DbReconcileRow row)
    {
        return RealtimeKeyBuilder.ExactKey(row.Building, row.Floor, row.SubArea, row.PageName, row.Name);
    }

    private static string ExactKey(RealtimeDetailRecord row)
    {
        return RealtimeKeyBuilder.ExactKey(row);
    }

    private static string NameFloorKey(DbReconcileRow row)
    {
        return RealtimeKeyBuilder.NameFloorKey(row.Building, row.Floor, row.SubArea, row.Name);
    }

    private static string NameFloorKey(RealtimeDetailRecord row)
    {
        return RealtimeKeyBuilder.NameFloorKey(row.Building, row.Floor, row.SubArea, row.Name);
    }

    private static bool MatchesType(RealtimeReconciliationItem item, string? diffType)
    {
        return string.IsNullOrWhiteSpace(diffType) ||
               string.Equals(item.Type, diffType.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearch(RealtimeReconciliationItem item, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var q = searchText.Trim();
        return new[]
        {
            item.Building,
            item.FloorLabel,
            item.Name,
            item.DbLocation,
            item.RealtimeLocation,
            item.DevId,
            item.OverrideAction,
            item.Reason,
            item.RuleDescription,
            item.EvidenceSummary,
            string.Join(" ", item.DecisionPath),
        }.Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveBuildings(string? building)
    {
        var values = (building ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => BuildingOrder.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count == 0 ? BuildingOrder : values;
    }

    private static int TypeSort(string type)
    {
        return type switch
        {
            RealtimeReconciliationTypes.NewDevice => 0,
            RealtimeReconciliationTypes.MissingInRealtime => 1,
            RealtimeReconciliationTypes.MatchFailed => 2,
            RealtimeReconciliationTypes.VirtualOverride => 3,
            RealtimeReconciliationTypes.DuplicateRender => 4,
            RealtimeReconciliationTypes.DataNoise => 5,
            _ => 99,
        };
    }

    private static string ReadString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
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

    private sealed record DbReconcileRow(
        long Id,
        string Building,
        double? Floor,
        string FloorLabel,
        string SubArea,
        string PageName,
        string Name,
        string SwitchState,
        string CommunicationText);

    private sealed class ReconcileState
    {
        public ReconcileState(
            IReadOnlyList<DbReconcileRow> dbRows,
            IReadOnlyList<RealtimeDetailRecord> realtimeRows,
            RealtimeMatchOverrideSet overrides)
        {
            DbRows = dbRows;
            RealtimeRows = realtimeRows;
            Overrides = overrides;
            DbById = dbRows.ToDictionary(row => row.Id);
            DbByExact = BuildDbIndex(dbRows, ExactKey);
            DbByNameFloor = BuildDbIndex(dbRows, NameFloorKey);
        }

        public IReadOnlyList<DbReconcileRow> DbRows { get; }

        public IReadOnlyList<RealtimeDetailRecord> RealtimeRows { get; }

        public RealtimeMatchOverrideSet Overrides { get; }

        public IReadOnlyDictionary<long, DbReconcileRow> DbById { get; }

        public IReadOnlyDictionary<string, List<DbReconcileRow>> DbByExact { get; }

        public IReadOnlyDictionary<string, List<DbReconcileRow>> DbByNameFloor { get; }

        public HashSet<long> DbConsumed { get; } = [];

        public HashSet<string> RealtimeConsumed { get; } = [];

        public List<RealtimeReconciliationItem> Items { get; } = [];

        public int ExactMatches { get; set; }

        public int ManualMatches { get; set; }

        public int RelaxedMatches { get; set; }
    }
}
