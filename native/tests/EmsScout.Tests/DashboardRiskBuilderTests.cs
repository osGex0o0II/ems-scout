using EmsScout.Application;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Quality;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class DashboardRiskBuilderTests
{
    [Fact]
    public void BuildsActionableRisksFromAuditRealtimeRunsAndWatch()
    {
        var summary = new FleetSummary(
            Total: 10,
            Running: 3,
            Stopped: 5,
            Offline: 2,
            Unknown: 0,
            Buildings: []);
        var facets = new DeviceFacets(
            Total: 10,
            Running: 3,
            Stopped: 5,
            Offline: 2,
            Unknown: 0,
            PublicArea: 2,
            PrivateArea: 8,
            TemperatureIssues: 1,
            NeedsReview: 3,
            WatchAbnormal: 1);
        var quality = new QualityAuditReport(
            SourcePath: "quality_report.json",
            GeneratedAt: "2026-07-06T08:00:00Z",
            GeneratedAtLocal: "2026-07-06 16:00",
            RunId: 7,
            Summary: new QualityAuditSummary(
                TotalCards: 10,
                IssueCount: 2,
                PlaceholderCards: 0,
                StateMismatch: 0,
                UnknownCommunication: 1,
                MissingIndicator: 1,
                UnknownSwitch: 0,
                DuplicateCardsSamePage: 0,
                DuplicateRenderedPages: 0,
                EmptySubAreas: 0,
                InlineSubAreas: 0,
                SuspiciousUniformPages: 0,
                UniformResolvedPages: 0),
            Issues: [],
            IsStale: false,
            StaleReason: string.Empty);
        var realtime = new RealtimeQualityAuditReport(
            SourcePath: "realtime_quality.json",
            CreatedAt: "2026-07-06T08:00:00Z",
            SummarySource: "summary.json",
            TotalRows: 10,
            UniqueDevices: 10,
            CollectionOk: true,
            CollectionErrorCount: 0,
            DeviceAnomalyRows: 2,
            DeviceAnomalyEvents: 3,
            CollectionErrorCategories: [],
            DeviceAnomalyCategories: [],
            Buildings: [],
            Note: string.Empty);
        var reconciliation = new RealtimeReconciliationSummary(
            DbCount: 10,
            RealtimeCount: 9,
            Difference: -1,
            DiffItemCount: 1,
            ExactMatches: 8,
            ManualMatches: 0,
            RelaxedMatches: 1,
            OverrideCount: 0,
            ByType: new Dictionary<string, int> { [RealtimeReconciliationTypes.MissingInRealtime] = 1 },
            GeneratedAt: DateTimeOffset.UtcNow);
        var runs = new[]
        {
            new CollectionRunRecord(
                Id: 1,
                RunKey: "run",
                StartedAt: string.Empty,
                CompletedAt: string.Empty,
                ImportedAt: string.Empty,
                Status: "completed",
                Scope: "full",
                Buildings: [],
                JsonPath: string.Empty,
                DbSnapshotPath: string.Empty,
                CardCount: 10,
                OnCount: 3,
                OffCount: 5,
                OfflineCount: 2,
                UnknownCount: 0,
                QualitySummary: "{}",
                IsAnomaly: true,
                Note: "采集数据异常，已隔离"),
        };

        var risks = DashboardRiskBuilder.Build(
            summary,
            facets,
            quality,
            qualityError: null,
            realtime,
            realtimeError: null,
            reconciliation,
            reconciliationError: null,
            runs,
            runsError: null);

        Assert.Contains(risks, risk => risk.Title == "关注设备发生开关变化" && risk.WatchState == "abnormal");
        Assert.Contains(risks, risk => risk.Title == "基础质量审计存在问题" && risk.QuickFilter == "needs_review");
        Assert.Contains(risks, risk => risk.Title == "实时设备字段存在异常" && risk.RealtimeMatch == "invalid");
        Assert.Contains(risks, risk => risk.Title == "实时源存在对账差异" && risk.RealtimeMatch == "missing");
        Assert.Contains(risks, risk => risk.Title == "存在异常隔离批次");
        Assert.Contains(risks, risk => risk.IssueId == "watch:state:abnormal" && risk.SourceKey == "watch");
        Assert.Contains(risks, risk => risk.IssueId == "quality:summary:issues" && risk.SourceKey == "quality");
        Assert.Contains(risks, risk => risk.IssueId == "realtime:devices:invalid" && risk.SourceKey == "realtime");
        Assert.Contains(risks, risk => risk.IssueId == "reconciliation:summary:difference" && risk.SourceKey == "reconciliation");
        Assert.Contains(risks, risk => risk.IssueId == "runs:anomaly" && risk.SourceKey == "runs");
    }

    [Fact]
    public void AddsSuccessRiskWhenNoRiskSourcesReportIssues()
    {
        var summary = new FleetSummary(
            Total: 3,
            Running: 1,
            Stopped: 2,
            Offline: 0,
            Unknown: 0,
            Buildings: []);
        var facets = new DeviceFacets(
            Total: 3,
            Running: 1,
            Stopped: 2,
            Offline: 0,
            Unknown: 0,
            PublicArea: 1,
            PrivateArea: 2,
            TemperatureIssues: 0,
            NeedsReview: 0);

        var risks = DashboardRiskBuilder.Build(
            summary,
            facets,
            qualityReport: new QualityAuditReport(
                SourcePath: "quality_report.json",
                GeneratedAt: "2026-07-06T08:00:00Z",
                GeneratedAtLocal: "2026-07-06 16:00",
                RunId: 1,
                Summary: new QualityAuditSummary(
                    TotalCards: 3,
                    IssueCount: 0,
                    PlaceholderCards: 0,
                    StateMismatch: 0,
                    UnknownCommunication: 0,
                    MissingIndicator: 0,
                    UnknownSwitch: 0,
                    DuplicateCardsSamePage: 0,
                    DuplicateRenderedPages: 0,
                    EmptySubAreas: 0,
                    InlineSubAreas: 0,
                    SuspiciousUniformPages: 0,
                    UniformResolvedPages: 0),
                Issues: [],
                IsStale: false,
                StaleReason: string.Empty),
            qualityError: null,
            realtimeReport: new RealtimeQualityAuditReport(
                SourcePath: "realtime_quality.json",
                CreatedAt: "2026-07-06T08:00:00Z",
                SummarySource: "summary.json",
                TotalRows: 3,
                UniqueDevices: 3,
                CollectionOk: true,
                CollectionErrorCount: 0,
                DeviceAnomalyRows: 0,
                DeviceAnomalyEvents: 0,
                CollectionErrorCategories: [],
                DeviceAnomalyCategories: [],
                Buildings: [],
                Note: string.Empty),
            realtimeError: null,
            reconciliation: new RealtimeReconciliationSummary(
                DbCount: 3,
                RealtimeCount: 3,
                Difference: 0,
                DiffItemCount: 0,
                ExactMatches: 3,
                ManualMatches: 0,
                RelaxedMatches: 0,
                OverrideCount: 0,
                ByType: new Dictionary<string, int>(),
                GeneratedAt: DateTimeOffset.UtcNow),
            reconciliationError: null,
            runs: [],
            runsError: null);

        Assert.Contains(risks, risk => risk.Kind == OverviewMetricKind.Success);
    }
}
