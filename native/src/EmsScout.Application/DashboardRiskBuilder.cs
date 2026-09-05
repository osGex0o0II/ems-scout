using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Quality;
using EmsScout.Domain;

namespace EmsScout.Application;

public static class DashboardRiskBuilder
{
    public static IReadOnlyList<DashboardRiskItem> Build(
        FleetSummary summary,
        DeviceFacets facets,
        QualityAuditReport? qualityReport,
        Exception? qualityError,
        RealtimeQualityAuditReport? realtimeReport,
        Exception? realtimeError,
        RealtimeReconciliationSummary? reconciliation,
        Exception? reconciliationError,
        IReadOnlyList<CollectionRunRecord> runs,
        Exception? runsError)
    {
        var risks = new List<DashboardRiskItem>();

        if (summary.Total == 0)
        {
            risks.Add(new DashboardRiskItem(
                "当前没有设备数据",
                "总览、数据管理和 Excel 导出都需要先完成采集并导入 SQLite。",
                "当前数据",
                OverviewMetricKind.Danger));
        }

        if (summary.Unknown > 0)
        {
            risks.Add(new DashboardRiskItem(
                "存在未知通讯状态",
                $"当前 {summary.Unknown:N0} 台设备状态未判定，需要复核 indicator 映射或采集质量。",
                "当前数据",
                OverviewMetricKind.Warning,
                summary.Unknown,
                "查看未知",
                CommunicationState: "未知"));
        }

        if (summary.Offline > 0)
        {
            risks.Add(new DashboardRiskItem(
                "存在离线设备",
                $"当前 {summary.Offline:N0} 台设备离线；建议先按楼栋和区域筛查。",
                "当前数据",
                summary.OfflineRate >= 0.1 ? OverviewMetricKind.Warning : OverviewMetricKind.Info,
                summary.Offline,
                "查看离线",
                CommunicationState: "离线"));
        }

        if (facets.WatchAbnormal > 0)
        {
            risks.Add(new DashboardRiskItem(
                "关注设备发生开关变化",
                $"关注时间窗内有 {facets.WatchAbnormal:N0} 台设备发生 ON/OFF 变化，数据管理会显示证据批次。",
                "关注设备",
                OverviewMetricKind.Danger,
                facets.WatchAbnormal,
                "查看异常",
                WatchState: "abnormal"));
        }

        AddQualityRisk(risks, qualityReport, qualityError);
        AddRealtimeQualityRisk(risks, realtimeReport, realtimeError);
        AddReconciliationRisk(risks, reconciliation, reconciliationError);
        AddRunRisk(risks, runs, runsError);

        if (risks.Count == 0)
        {
            risks.Add(new DashboardRiskItem(
                "未发现高优先级风险",
                "基础质量、实时审计、实时对账、历史批次和关注设备均未报告需要立即处理的项目。",
                "总览",
                OverviewMetricKind.Success));
        }

        return risks;
    }

    private static void AddQualityRisk(
        ICollection<DashboardRiskItem> risks,
        QualityAuditReport? report,
        Exception? error)
    {
        if (error is not null)
        {
            risks.Add(new DashboardRiskItem(
                "基础质量审计读取失败",
                error.Message,
                "基础质量审计",
                OverviewMetricKind.Warning));
            return;
        }

        if (report is null)
        {
            risks.Add(new DashboardRiskItem(
                "缺少基础质量审计结果",
                "采集任务完成后建议运行基础质量检查，避免用过期或未审计数据判断进度。",
                "基础质量审计",
                OverviewMetricKind.Warning));
            return;
        }

        if (report.IsStale)
        {
            risks.Add(new DashboardRiskItem(
                "基础质量审计可能过期",
                string.IsNullOrWhiteSpace(report.StaleReason)
                    ? "质量审计结果早于当前数据。"
                    : report.StaleReason,
                "基础质量审计",
                OverviewMetricKind.Warning,
                report.Summary.IssueCount));
        }

        if (report.Summary.IssueCount > 0)
        {
            risks.Add(new DashboardRiskItem(
                "基础质量审计存在问题",
                $"共 {report.Summary.IssueCount:N0} 项质量问题；未知通讯 {report.Summary.UnknownCommunication:N0}，缺 indicator {report.Summary.MissingIndicator:N0}。",
                "基础质量审计",
                OverviewMetricKind.Warning,
                report.Summary.IssueCount,
                "查看需排查",
                QuickFilter: "needs_review"));
        }
    }

    private static void AddRealtimeQualityRisk(
        ICollection<DashboardRiskItem> risks,
        RealtimeQualityAuditReport? report,
        Exception? error)
    {
        if (error is not null)
        {
            risks.Add(new DashboardRiskItem(
                "实时点位审计读取失败",
                error.Message,
                "实时点位审计",
                OverviewMetricKind.Warning));
            return;
        }

        if (report is null)
        {
            risks.Add(new DashboardRiskItem(
                "缺少实时点位审计结果",
                "运行实时详情采集和点位审计后，首页才能判断实时字段覆盖和异常。",
                "实时点位审计",
                OverviewMetricKind.Info));
            return;
        }

        if (!report.CollectionOk)
        {
            risks.Add(new DashboardRiskItem(
                "实时采集存在阻断错误",
                $"采集错误 {report.CollectionErrorCount:N0} 项；应先处理实时详情采集质量。",
                "实时点位审计",
                OverviewMetricKind.Danger,
                report.CollectionErrorCount,
                "查看点位异常",
                RealtimePoints: "incomplete"));
        }

        if (report.DeviceAnomalyRows > 0)
        {
            risks.Add(new DashboardRiskItem(
                "实时设备字段存在异常",
                $"异常设备 {report.DeviceAnomalyRows:N0} 台，异常事件 {report.DeviceAnomalyEvents:N0} 项。",
                "实时点位审计",
                OverviewMetricKind.Warning,
                report.DeviceAnomalyRows,
                "查看详情异常",
                RealtimeMatch: "invalid"));
        }
    }

    private static void AddReconciliationRisk(
        ICollection<DashboardRiskItem> risks,
        RealtimeReconciliationSummary? summary,
        Exception? error)
    {
        if (error is not null)
        {
            risks.Add(new DashboardRiskItem(
                "实时对账读取失败",
                error.Message,
                "实时对账",
                OverviewMetricKind.Warning));
            return;
        }

        if (summary is null)
        {
            return;
        }

        if (summary.DiffItemCount > 0)
        {
            risks.Add(new DashboardRiskItem(
                "实时源存在对账差异",
                $"差异 {summary.DiffItemCount:N0} 项；DB {summary.DbCount:N0}，实时 {summary.RealtimeCount:N0}，差额 {summary.Difference:+#;-#;0}。",
                "实时对账",
                OverviewMetricKind.Warning,
                summary.DiffItemCount,
                "查看缺实时",
                RealtimeMatch: "missing"));
        }
    }

    private static void AddRunRisk(
        ICollection<DashboardRiskItem> risks,
        IReadOnlyList<CollectionRunRecord> runs,
        Exception? error)
    {
        if (error is not null)
        {
            risks.Add(new DashboardRiskItem(
                "历史批次读取失败",
                error.Message,
                "历史批次",
                OverviewMetricKind.Warning));
            return;
        }

        var anomalies = runs.Count(run => run.IsAnomaly);
        if (anomalies > 0)
        {
            risks.Add(new DashboardRiskItem(
                "存在异常隔离批次",
                $"已有 {anomalies:N0} 个历史批次被标记异常；恢复或对比当前数据前应确认隔离原因。",
                "历史批次",
                OverviewMetricKind.Warning,
                anomalies));
        }
    }
}
