namespace EmsScout.Application.Collection;

public sealed record CollectionTaskModeOption(
    string Value,
    string Label,
    string Description,
    string StartButtonText);

public static class CollectionTaskModeValues
{
    public const string Full = "full";
    public const string CollectImport = "collect_import";
    public const string Recapture = "recapture";
    public const string EnumerateOnly = "enumerate_only";
    public const string ValidateOnly = "validate_only";
    public const string ImportOnly = "import_only";
    public const string QualityOnly = "quality_only";
    public const string RealtimeDetailsOnly = "realtime_details_only";
    public const string RealtimeAuditOnly = "realtime_audit_only";
    public const string Custom = "custom";
}

public sealed record CollectionTaskExecutionPlan(
    string Value,
    string Label,
    bool RequiresBuildings,
    bool RunEnumeration,
    bool RunValidation,
    bool RunImport,
    bool RunQuality,
    bool RunRealtimeDetails,
    bool RunRealtimeAudit)
{
    public string RunningStatus => RunEnumeration
        ? "正在执行：" + Label
        : "正在运行：" + Label;

    public string CompletedStatus(bool imported, bool realtimeDetailsUpdated)
    {
        if (imported || realtimeDetailsUpdated)
        {
            return "任务完成，当前数据已更新";
        }

        return RunEnumeration
            ? "枚举完成，未导入 SQLite"
            : "任务完成";
    }
}

public sealed record CollectionCustomTaskOptions(
    bool RunImportAfterCollect,
    bool RunQualityAfterImport,
    bool RunRealtimeDetailsAfterImport,
    bool RunRealtimeAuditAfterDetails);

public static class CollectionTaskModeCatalog
{
    public static IReadOnlyList<CollectionTaskModeOption> Options { get; } =
    [
        new(CollectionTaskModeValues.CollectImport, "采集并导入（兼容）", "兼容旧流程：采集、校验、导入并运行基础质量检查。", "开始兼容任务"),
        new(CollectionTaskModeValues.Full, "采集", "采集所选楼栋、更新当前数据并完成质量与实时审计。", "开始采集"),
        new(CollectionTaskModeValues.Recapture, "补采指定区域", "补采指定楼栋、座号或楼层，并完成完整数据更新与审计。", "开始补采"),
        new(CollectionTaskModeValues.EnumerateOnly, "仅枚举 JSON", "只运行卡片枚举，生成 enum_full_v5.json，不更新 SQLite。", "开始枚举"),
        new(CollectionTaskModeValues.ValidateOnly, "仅校验 JSON", "只校验现有 enum_full_v5.json，不修改 SQLite。", "开始校验"),
        new(CollectionTaskModeValues.ImportOnly, "仅导入 SQLite", "将现有 enum_full_v5.json 导入 SQLite；导入脚本会再次校验采集结果。", "开始导入"),
        new(CollectionTaskModeValues.QualityOnly, "仅基础审计", "只运行基础质量审计，并生成绑定最新批次的不可变报告。", "开始基础审计"),
        new(CollectionTaskModeValues.RealtimeDetailsOnly, "仅实时详情", "只采集实时详情并刷新实时对账，不重新枚举卡片。", "开始实时详情"),
        new(CollectionTaskModeValues.RealtimeAuditOnly, "仅实时审计", "只审计已有实时详情数据，不重新采集。", "开始实时审计"),
        new(CollectionTaskModeValues.Custom, "自定义流程", "按下方开关组合执行，适合补采、排障或临时流程。", "开始自定义任务"),
    ];

    public static CollectionTaskExecutionPlan BuildPlan(
        string? modeValue,
        CollectionCustomTaskOptions customOptions)
    {
        var value = string.IsNullOrWhiteSpace(modeValue)
            ? CollectionTaskModeValues.Full
            : modeValue.Trim();

        return value switch
        {
            CollectionTaskModeValues.Full => new(
                value,
                "采集",
                RequiresBuildings: true,
                RunEnumeration: true,
                RunValidation: true,
                RunImport: true,
                RunQuality: true,
                RunRealtimeDetails: true,
                RunRealtimeAudit: true),
            CollectionTaskModeValues.CollectImport => new(
                value,
                "采集并导入",
                RequiresBuildings: true,
                RunEnumeration: true,
                RunValidation: true,
                RunImport: true,
                RunQuality: true,
                RunRealtimeDetails: false,
                RunRealtimeAudit: false),
            CollectionTaskModeValues.Recapture => new(
                value,
                "补采指定区域",
                RequiresBuildings: true,
                RunEnumeration: true,
                RunValidation: true,
                RunImport: true,
                RunQuality: true,
                RunRealtimeDetails: true,
                RunRealtimeAudit: true),
            CollectionTaskModeValues.EnumerateOnly => new(
                value,
                "仅枚举 JSON",
                RequiresBuildings: true,
                RunEnumeration: true,
                RunValidation: false,
                RunImport: false,
                RunQuality: false,
                RunRealtimeDetails: false,
                RunRealtimeAudit: false),
            CollectionTaskModeValues.ValidateOnly => new(
                value,
                "仅校验 JSON",
                RequiresBuildings: false,
                RunEnumeration: false,
                RunValidation: true,
                RunImport: false,
                RunQuality: false,
                RunRealtimeDetails: false,
                RunRealtimeAudit: false),
            CollectionTaskModeValues.ImportOnly => new(
                value,
                "仅导入 SQLite",
                RequiresBuildings: true,
                RunEnumeration: false,
                RunValidation: false,
                RunImport: true,
                RunQuality: false,
                RunRealtimeDetails: false,
                RunRealtimeAudit: false),
            CollectionTaskModeValues.QualityOnly => new(
                value,
                "仅基础审计",
                RequiresBuildings: false,
                RunEnumeration: false,
                RunValidation: false,
                RunImport: false,
                RunQuality: true,
                RunRealtimeDetails: false,
                RunRealtimeAudit: false),
            CollectionTaskModeValues.RealtimeDetailsOnly => new(
                value,
                "仅实时详情",
                RequiresBuildings: true,
                RunEnumeration: false,
                RunValidation: false,
                RunImport: false,
                RunQuality: false,
                RunRealtimeDetails: true,
                RunRealtimeAudit: true),
            CollectionTaskModeValues.RealtimeAuditOnly => new(
                value,
                "仅实时审计",
                RequiresBuildings: false,
                RunEnumeration: false,
                RunValidation: false,
                RunImport: false,
                RunQuality: false,
                RunRealtimeDetails: false,
                RunRealtimeAudit: true),
            CollectionTaskModeValues.Custom => new(
                value,
                "自定义流程",
                RequiresBuildings: true,
                RunEnumeration: true,
                RunValidation: customOptions.RunImportAfterCollect,
                RunImport: customOptions.RunImportAfterCollect,
                RunQuality: customOptions.RunImportAfterCollect && customOptions.RunQualityAfterImport,
                RunRealtimeDetails: customOptions.RunRealtimeDetailsAfterImport,
                RunRealtimeAudit: customOptions.RunRealtimeDetailsAfterImport && customOptions.RunRealtimeAuditAfterDetails),
            _ => BuildPlan(CollectionTaskModeValues.Full, customOptions),
        };
    }
}
