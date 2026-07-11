using EmsScout.Application.Quality;

namespace EmsScout.Infrastructure.Quality;

internal sealed class QualityAuditAnalyzer
{
    public QualityAuditAnalysis Analyze(
        QualityAuditDataSet data,
        KnownQualityFindingCatalog knownFindings)
    {
        var subAreasById = data.SubAreas.ToDictionary(row => row.Id);
        var pagesById = data.Pages
            .Where(page => subAreasById.ContainsKey(page.SubAreaId))
            .ToDictionary(page => page.Id);
        var cardsByPage = data.Cards
            .Where(card => pagesById.ContainsKey(card.PageId))
            .GroupBy(card => card.PageId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<QualityAuditCard>)group.ToList());
        var pageContexts = pagesById.Values
            .Select(page => new PageContext(
                subAreasById[page.SubAreaId],
                page,
                cardsByPage.GetValueOrDefault(page.Id) ?? []))
            .ToList();
        var cardContexts = pageContexts
            .SelectMany(page => page.Cards.Select(card => new CardContext(page.SubArea, page.Page, card)))
            .ToList();

        var placeholderCards = cardContexts
            .Where(row => string.IsNullOrEmpty(row.Card.Name) || row.Card.Name == "0-0001-KT")
            .ToList();
        var inconsistentState = cardContexts.Where(row => IsStateMismatch(row.Card)).ToList();
        var unknownCommunication = cardContexts
            .Where(row => string.IsNullOrEmpty(row.Card.Communication))
            .ToList();
        var missingIndicator = cardContexts
            .Where(row => string.IsNullOrEmpty(row.Card.Indicator) && row.Card.Communication != "离线")
            .ToList();
        var unknownSwitch = cardContexts
            .Where(row => row.Card.Switch is not ("ON" or "OFF" or "-"))
            .ToList();
        var duplicateCardsSamePage = pageContexts
            .Sum(page => page.Cards
                .Where(card => !string.IsNullOrEmpty(card.Name))
                .GroupBy(card => card.Name, StringComparer.Ordinal)
                .Count(group => group.Count() > 1));
        var duplicateRenderedPages = pageContexts
            .Where(row => IsDuplicateRenderedPage(row.Page))
            .ToList();
        var emptySubAreas = data.SubAreas
            .Where(subArea => pageContexts.All(page => page.SubArea.Id != subArea.Id))
            .ToList();
        var inlineSubAreas = emptySubAreas.Where(IsInlineSubArea).ToList();
        var emptyNonInlineSubAreas = emptySubAreas.Where(row => !IsInlineSubArea(row)).ToList();
        var suspiciousUniformPages = pageContexts.Where(IsSuspiciousUniformPage).ToList();
        var uniformResolvedPages = pageContexts
            .Where(IsUniformResolvedPage)
            .Select(CreateUniformPage)
            .ToList();
        var offlineTemplateWithoutStability = uniformResolvedPages
            .Where(row => IsOfflineTemplate(row) && row.Context.Page.QualityReason != "offline_template_stable")
            .ToList();
        var offlineTemplateStable = uniformResolvedPages
            .Where(row => IsOfflineTemplate(row) && row.Context.Page.QualityReason == "offline_template_stable")
            .ToList();
        var invalidCardFields = cardContexts.Where(row => HasInvalidCardFields(row.Card)).ToList();
        var activeFieldIncompletePages = pageContexts.Where(HasIncompleteActiveFields).ToList();

        var unknownCommunicationClassification = knownFindings.Classify(
            unknownCommunication,
            "unknown_comm",
            CardSubject);
        var missingIndicatorClassification = knownFindings.Classify(
            missingIndicator,
            "missing_indicator",
            CardSubject);
        var offlineWithoutStabilityClassification = knownFindings.Classify(
            offlineTemplateWithoutStability,
            "offline_template_without_stability",
            row => PageSubject(row.Context));
        var offlineStableClassification = knownFindings.Classify(
            offlineTemplateStable,
            "offline_template_stable",
            row => PageSubject(row.Context));
        var invalidCardClassification = knownFindings.Classify(
            invalidCardFields,
            "invalid_card_fields",
            CardSubject);
        var activeFieldClassification = knownFindings.Classify(
            activeFieldIncompletePages,
            "active_field_incomplete_pages",
            PageSubject);
        var annotations = new[]
            {
                unknownCommunicationClassification.Annotations,
                missingIndicatorClassification.Annotations,
                offlineWithoutStabilityClassification.Annotations,
                offlineStableClassification.Annotations,
                invalidCardClassification.Annotations,
                activeFieldClassification.Annotations,
            }
            .SelectMany(rows => rows)
            .ToList();

        var issues = new List<QualityAuditIssue>();
        AddIssue(issues, "P1", "placeholder_names", placeholderCards.Count, "存在 0-0001-KT 或空卡名，说明页面未完全加载即入库。");
        AddIssue(issues, "P1", "state_mismatch", inconsistentState.Count, "comm 与 switch 不一致。");
        AddIssue(issues, "P2", "unknown_switch", unknownSwitch.Count, "存在非 ON/OFF/- 的开关状态。");
        AddIssue(issues, "P2", "duplicate_cards_same_page", duplicateCardsSamePage, "同一页面存在重复卡名。");
        AddIssue(issues, "P2", "empty_sub_areas", emptyNonInlineSubAreas.Count, "存在无页面/无卡片的空子区。");
        AddIssue(issues, "P2", "suspicious_uniform_pages", suspiciousUniformPages.Count, "存在统一默认值且未完整加载通讯/开关的页面。");
        AddIssue(issues, "P2", "unknown_comm", unknownCommunicationClassification.BlockingRows.Count, "存在未知通讯状态。");
        AddIssue(issues, "P2", "missing_indicator", missingIndicatorClassification.BlockingRows.Count, "存在非离线卡缺少 indicator 原图。");
        AddIssue(issues, "P2", "offline_template_without_stability", offlineWithoutStabilityClassification.BlockingRows.Count, "存在全离线默认模板页，但缺少采集稳定窗口证据。");
        AddIssue(issues, "P2", "offline_template_stable", offlineStableClassification.BlockingRows.Count, "存在全离线默认模板页；虽已观察到稳定窗口，仍需人工复核 EMS 是否真实全离线。");
        AddIssue(issues, "P1", "invalid_card_fields", invalidCardClassification.BlockingRows.Count, "存在异常温度或开机/关机设备字段缺失。");
        AddIssue(issues, "P1", "active_field_incomplete_pages", activeFieldClassification.BlockingRows.Count, "存在开机/关机设备字段不完整的页面。");
        AddIssue(issues, "INFO", "known_findings", annotations.Count, "存在已登记的质量发现；未接受状态仍会阻断通过。");
        AddIssue(issues, "INFO", "duplicate_rendered_pages", duplicateRenderedPages.Count, "存在 EMS 同页重复渲染卡，入库已按卡名去重。");
        AddIssue(issues, "INFO", "uniform_resolved_pages", uniformResolvedPages.Count, "存在字段完全统一但状态完整的页面，通常为全离线或模板式真实页。");
        AddIssue(issues, "INFO", "inline_sub_area", inlineSubAreas.Count, "6号 BM 通过 A座 1F 的 BM page 采集，空 BM 子区为占位记录。");

        var summary = new QualityAuditSummary(
            TotalCards: cardContexts.Count,
            IssueCount: issues.Count(issue => issue.Severity != "INFO"),
            PlaceholderCards: placeholderCards.Count,
            StateMismatch: inconsistentState.Count,
            UnknownCommunication: unknownCommunication.Count,
            MissingIndicator: missingIndicator.Count,
            UnknownSwitch: unknownSwitch.Count,
            DuplicateCardsSamePage: duplicateCardsSamePage,
            DuplicateRenderedPages: duplicateRenderedPages.Count,
            EmptySubAreas: emptyNonInlineSubAreas.Count,
            InlineSubAreas: inlineSubAreas.Count,
            SuspiciousUniformPages: suspiciousUniformPages.Count,
            UniformResolvedPages: uniformResolvedPages.Count)
        {
            KnownFindings = annotations.Count,
            BlockingKnownFindings = annotations.Count(annotation => annotation.IsBlocking),
            NonBlockingKnownFindings = annotations.Count(annotation => !annotation.IsBlocking),
            OfflineTemplateWithoutStability = offlineWithoutStabilityClassification.BlockingRows.Count,
            OfflineTemplateStable = offlineStableClassification.BlockingRows.Count,
            InvalidCardFields = invalidCardClassification.BlockingRows.Count,
            ActiveFieldIncompletePages = activeFieldClassification.BlockingRows.Count,
            DetectedOfflineTemplateWithoutStability = offlineTemplateWithoutStability.Count,
            DetectedOfflineTemplateStable = offlineTemplateStable.Count,
            DetectedInvalidCardFields = invalidCardFields.Count,
            DetectedActiveFieldIncompletePages = activeFieldIncompletePages.Count,
        };

        return new QualityAuditAnalysis(summary, issues, annotations);
    }

    private static bool IsStateMismatch(QualityAuditCard card) =>
        card.Communication switch
        {
            "开机" => card.Switch != "ON",
            "关机" => card.Switch != "OFF",
            "离线" => card.Switch != "-",
            _ => false,
        };

    private static bool IsDuplicateRenderedPage(QualityAuditPage page)
    {
        var raw = page.RawCount ?? page.Count;
        var unique = page.UniqueCount ?? page.Count;
        return raw is not null && unique is not null && raw > unique;
    }

    private static bool IsInlineSubArea(QualityAuditSubArea row) =>
        row.Building == "6号" && row.Floor == -2 && row.Text == "BM";

    private static bool IsSuspiciousUniformPage(PageContext row)
    {
        var quality = CollectionPageQualityRules.Evaluate(
            row.Cards.Select(ToPageQualityCard).ToList(),
            new CollectionPageQualityMeta(row.Page.RawCount, row.Page.UniqueCount));
        if (!quality.UniformTemplate)
        {
            return false;
        }

        var placeholders = row.Cards.Count(card => card.Name == "0-0001-KT");
        var withCommunication = row.Cards.Count(card => !string.IsNullOrEmpty(card.Communication));
        var withResolvedState = row.Cards.Count(card => IsResolvedCommunication(card.Communication));
        return placeholders > 0 ||
               withCommunication < row.Cards.Count ||
               withResolvedState < row.Cards.Count;
    }

    private static bool IsUniformResolvedPage(PageContext row) =>
        row.Cards.Count >= 2 &&
        HasUniformFields(row.Cards) &&
        row.Cards.All(card => IsResolvedCommunication(card.Communication));

    private static bool HasUniformFields(IReadOnlyList<QualityAuditCard> cards) =>
        DistinctNonNullCount(cards.Select(card => card.Indoor)) <= 1 &&
        DistinctNonNullCount(cards.Select(card => card.SetTemp)) <= 1 &&
        DistinctNonNullCount(cards.Select(card => card.Fan)) <= 1 &&
        DistinctNonNullCount(cards.Select(card => card.Mode)) <= 1;

    private static int DistinctNonNullCount(IEnumerable<string?> values) =>
        values.Where(value => value is not null).Distinct(StringComparer.Ordinal).Count();

    private static UniformPage CreateUniformPage(PageContext context) =>
        new(
            context,
            MinimumNonNull(context.Cards.Select(card => card.Indoor)),
            MinimumNonNull(context.Cards.Select(card => card.SetTemp)),
            MinimumNonNull(context.Cards.Select(card => card.Fan)),
            MinimumNonNull(context.Cards.Select(card => card.Mode)),
            context.Cards.Count(card => card.Communication == "开机"),
            context.Cards.Count(card => card.Communication == "关机"),
            context.Cards.Count(card => card.Communication == "离线"));

    private static string? MinimumNonNull(IEnumerable<string?> values) =>
        values
            .Where(value => value is not null)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsOfflineTemplate(UniformPage row) =>
        row.Offline == row.Context.Cards.Count &&
        CollectionPageQualityRules.IsKnownDefaultTemplateValues(
            row.Indoor,
            row.SetTemp,
            row.Fan,
            row.Mode);

    private static bool HasInvalidCardFields(QualityAuditCard card) =>
        CollectionPageQualityRules.HasInvalidCardFields(ToPageQualityCard(card));

    private static bool HasIncompleteActiveFields(PageContext row)
    {
        var active = row.Cards.Where(card => IsActive(card.Communication)).ToList();
        if (active.Count == 0)
        {
            return false;
        }

        return active.Any(HasInvalidCardFields);
    }

    private static CollectionPageQualityCard ToPageQualityCard(QualityAuditCard card) =>
        new(
            card.Name,
            card.Switch,
            card.Mode,
            card.Indoor,
            card.SetTemp,
            card.Fan,
            card.Indicator,
            card.Communication);

    private static bool IsActive(string? communication) => communication is "开机" or "关机";

    private static bool IsResolvedCommunication(string? communication) =>
        communication is "开机" or "关机" or "离线";

    private static QualityFindingSubject CardSubject(CardContext row) =>
        new(
            row.SubArea.Building,
            row.SubArea.Floor,
            row.SubArea.Text,
            row.Page.Name,
            row.SubArea.X,
            row.SubArea.Y,
            row.Card.Name);

    private static QualityFindingSubject PageSubject(PageContext row) =>
        new(
            row.SubArea.Building,
            row.SubArea.Floor,
            row.SubArea.Text,
            row.Page.Name,
            row.SubArea.X,
            row.SubArea.Y);

    private static void AddIssue(
        ICollection<QualityAuditIssue> issues,
        string severity,
        string code,
        int count,
        string message)
    {
        if (count > 0)
        {
            issues.Add(new QualityAuditIssue(severity, code, count, message));
        }
    }

    private sealed record PageContext(
        QualityAuditSubArea SubArea,
        QualityAuditPage Page,
        IReadOnlyList<QualityAuditCard> Cards);

    private sealed record CardContext(
        QualityAuditSubArea SubArea,
        QualityAuditPage Page,
        QualityAuditCard Card);

    private sealed record UniformPage(
        PageContext Context,
        string? Indoor,
        string? SetTemp,
        string? Fan,
        string? Mode,
        int On,
        int Off,
        int Offline);
}

internal sealed record QualityAuditAnalysis(
    QualityAuditSummary Summary,
    IReadOnlyList<QualityAuditIssue> Issues,
    IReadOnlyList<QualityAuditKnownFindingAnnotation> KnownFindingAnnotations);
