using EmsScout.Application.Groups;

namespace EmsScout.Tests;

public sealed class AreaGroupEditingSessionTests
{
    [Fact]
    public void WatchLoadIsNotApplicableWithoutACustomGroup()
    {
        var session = new AreaGroupEditingSession();

        Assert.Equal("NotApplicable", WatchLoadState(session));

        var noGroup = BeginWatchLoad(session, groupId: null, isCustom: false);
        Assert.Equal("NotApplicable", WatchLoadState(session));
        Assert.Null(CurrentWatchGroupId(session));
        Assert.Equal(RequestGeneration(noGroup), WatchLoadGeneration(session));

        var systemGroup = BeginWatchLoad(session, groupId: 7, isCustom: false);
        Assert.Equal("NotApplicable", WatchLoadState(session));
        Assert.Equal(7, CurrentWatchGroupId(session));
        Assert.True(RequestGeneration(systemGroup) > RequestGeneration(noGroup));
    }

    [Fact]
    public void CustomWatchLoadStartsLoadingAndExplicitNullRuleBecomesReady()
    {
        var session = new AreaGroupEditingSession();

        var request = BeginWatchLoad(session, groupId: 11, isCustom: true);

        Assert.Equal("Loading", WatchLoadState(session));
        Assert.Equal(11, CurrentWatchGroupId(session));
        Assert.False(IsWatchReady(session, 11));
        Assert.True(TryMarkWatchReady(session, request));
        Assert.Equal("Ready", WatchLoadState(session));
        Assert.True(IsWatchReady(session, 11));
    }

    [Fact]
    public void SuccessfulRuleEvaluationBecomesReadyForTheCurrentGroup()
    {
        var session = new AreaGroupEditingSession();
        var request = BeginWatchLoad(session, groupId: 22, isCustom: true);

        Assert.True(TryMarkWatchReady(session, request));

        Assert.Equal("Ready", WatchLoadState(session));
        Assert.True(IsWatchReady(session, 22));
        Assert.False(IsWatchReady(session, 11));
    }

    [Fact]
    public void FailedWatchLoadRemainsFailedAndNonReadyUntilANewLoadBegins()
    {
        var session = new AreaGroupEditingSession();
        var failed = BeginWatchLoad(session, groupId: 11, isCustom: true);

        Assert.True(TryMarkWatchFailed(session, failed));
        Assert.Equal("Failed", WatchLoadState(session));
        Assert.False(IsWatchReady(session, 11));
        Assert.False(TryMarkWatchReady(session, failed));
        Assert.Equal("Failed", WatchLoadState(session));

        var retry = BeginWatchLoad(session, groupId: 11, isCustom: true);
        Assert.Equal("Loading", WatchLoadState(session));
        Assert.True(RequestGeneration(retry) > RequestGeneration(failed));
        Assert.True(TryMarkWatchReady(session, retry));
        Assert.True(IsWatchReady(session, 11));
    }

    [Fact]
    public void StaleWatchResponseCannotMutateTheNewGroupOrItsReadiness()
    {
        var session = new AreaGroupEditingSession();
        var groupA = BeginWatchLoad(session, groupId: 11, isCustom: true);
        var groupB = BeginWatchLoad(session, groupId: 22, isCustom: true);

        Assert.False(TryMarkWatchReady(session, groupA));
        Assert.False(TryMarkWatchFailed(session, groupA));
        Assert.Equal("Loading", WatchLoadState(session));
        Assert.Equal(22, CurrentWatchGroupId(session));
        Assert.False(IsWatchReady(session, 22));

        Assert.True(TryMarkWatchReady(session, groupB));
        Assert.Equal("Ready", WatchLoadState(session));
        Assert.True(IsWatchReady(session, 22));
    }

    [Fact]
    public void ReadyCaptureRejectsAnOlderGenerationForTheSameGroup()
    {
        var session = new AreaGroupEditingSession();
        var generationA = BeginWatchLoad(session, groupId: 11, isCustom: true);
        Assert.True(TryMarkWatchReady(session, generationA));
        Assert.True(TryCaptureReadyWatch(session, 11, out var readyA));

        var generationB = BeginWatchLoad(session, groupId: 11, isCustom: true);
        Assert.True(TryMarkWatchReady(session, generationB));
        Assert.True(TryCaptureReadyWatch(session, 11, out var readyB));

        Assert.False(IsWatchReady(session, readyA));
        Assert.True(IsWatchReady(session, readyB));
        Assert.True(RequestGeneration(readyB) > RequestGeneration(readyA));
    }

    [Fact]
    public void RejectsOlderMainLoadsAndKeepsSnapshotOnNewestRequest()
    {
        var session = new AreaGroupEditingSession();
        var firstSnapshot = Snapshot(AreaGroupSelectionKey.Create(11), selectedItemId: 101);
        var secondSnapshot = Snapshot(AreaGroupSelectionKey.Create(22), selectedItemId: 202);

        var first = session.BeginMainLoad(firstSnapshot);
        var second = session.BeginMainLoad(secondSnapshot);

        Assert.True(second.Version > first.Version);
        Assert.False(session.TryAcceptMain(first));
        Assert.True(session.TryAcceptMain(second));
        Assert.Same(secondSnapshot, second.Snapshot);
    }

    [Fact]
    public void PendingDraftsPreventOrdinarySelectionChanges()
    {
        var clean = Snapshot(AreaGroupSelectionKey.Create(11));
        var dirtyGroup = clean with
        {
            GroupDraft = clean.GroupDraft with { IsDirty = true },
        };
        var newGroup = clean with { IsNewGroupDraft = true };
        var activeMember = clean with
        {
            MemberDraft = clean.MemberDraft with { IsActive = true, Mode = "Adding" },
        };

        Assert.False(clean.HasPendingDraft);
        Assert.True(dirtyGroup.HasPendingDraft);
        Assert.True(newGroup.HasPendingDraft);
        Assert.True(activeMember.HasPendingDraft);
    }

    [Fact]
    public void CurrentMainFailureRetainsCapturedSnapshotAndRejectsStaleFailure()
    {
        var session = new AreaGroupEditingSession();
        var firstSnapshot = Snapshot(AreaGroupSelectionKey.Create(11), selectedItemId: 101);
        var secondSnapshot = Snapshot(AreaGroupSelectionKey.Create(22), selectedItemId: 202) with
        {
            GroupDraft = new AreaGroupEditorDraft("未保存", "夜班", "草稿", "紧急", false, IsDirty: true),
        };
        var first = session.BeginMainLoad(firstSnapshot);
        var second = session.BeginMainLoad(secondSnapshot);

        Assert.False(session.TryRetainMainOnFailure(first, out var staleSnapshot));
        Assert.Null(staleSnapshot);
        Assert.True(session.TryRetainMainOnFailure(second, out var retainedSnapshot));
        Assert.Same(secondSnapshot, retainedSnapshot);
    }

    [Fact]
    public void ActiveUidDraftWithoutCandidateRetainsStandaloneIdentityAcrossRefresh()
    {
        var session = new AreaGroupEditingSession();
        var snapshot = Snapshot(AreaGroupSelectionKey.Create(11)) with
        {
            MemberDraft = new AreaGroupMemberDraftSnapshot(
                IsActive: true,
                Mode: "Editing",
                EditingItemId: 88,
                TargetType: "device",
                Building: "6号",
                FloorLabel: "7F",
                Note: "草稿备注",
                SearchText: "已过滤掉当前候选",
                SubAreaText: "A座",
                CardName: "MOVED-KT",
                SelectedTarget: null,
                DeviceUid: "uid-active-draft"),
        };

        var request = session.BeginMainLoad(snapshot);

        Assert.True(session.TryRetainMainOnFailure(request, out var retained));
        Assert.NotNull(retained);
        Assert.Null(retained.MemberDraft.SelectedTarget);
        Assert.Equal("uid-active-draft", retained.MemberDraft.DeviceUid);
    }

    [Fact]
    public void FloorLoadsRequireNewestVersionAndCapturedBuilding()
    {
        var session = new AreaGroupEditingSession();
        var buildingA = session.BeginFloorLoad("1号");
        var buildingB = session.BeginFloorLoad("2号");

        Assert.False(session.TryAcceptFloor(buildingA, "1号"));
        Assert.False(session.TryAcceptFloor(buildingB, "1号"));
        Assert.True(session.TryAcceptFloor(buildingB, "2号"));
    }

    [Fact]
    public void ResolvesStoredAndDerivedSelectionsBeforeUsingDeterministicFallback()
    {
        var stored = AreaGroupSelectionKey.Create(42, areaFilter: "新筛选");
        var derived = AreaGroupSelectionKey.Create(
            null,
            areaFilter: " 公区 ",
            communicationFilter: " 开机 ");
        var fallback = AreaGroupSelectionKey.Create(7);
        var available = new[] { fallback, stored, derived };

        var storedResolution = Snapshot(AreaGroupSelectionKey.Create(42, areaFilter: "旧筛选")).ResolveGroupSelection(available);
        var derivedResolution = Snapshot(AreaGroupSelectionKey.Create(null, "公区", "开机")).ResolveGroupSelection(available);
        var deletedResolution = Snapshot(AreaGroupSelectionKey.Create(99)).ResolveGroupSelection(available);

        Assert.Equal(stored, storedResolution.Selection);
        Assert.True(storedResolution.StoredSelectionFound);
        Assert.Equal(derived, derivedResolution.Selection);
        Assert.True(derivedResolution.StoredSelectionFound);
        Assert.Equal(fallback, deletedResolution.Selection);
        Assert.False(deletedResolution.StoredSelectionFound);
    }

    [Fact]
    public void UnsavedNewGroupDraftKeepsNullSelectionAndAllDraftFields()
    {
        var groupDraft = new AreaGroupEditorDraft(
            "未保存名称",
            "夜班",
            "未保存说明",
            "紧急",
            false,
            IsDirty: true);
        var memberDraft = new AreaGroupMemberDraftSnapshot(
            IsActive: true,
            Mode: "Editing",
            EditingItemId: 88,
            TargetType: "device",
            Building: "6号",
            FloorLabel: "7F",
            Note: "草稿备注",
            SearchText: "KT-01",
            SubAreaText: "A座",
            CardName: "KT-0101",
            SelectedTarget: new AreaGroupTargetSelection("device", "6号", "7F", "A座", "KT-0101"));
        var snapshot = new AreaGroupEditingSnapshot(
            SelectedGroup: null,
            SelectedItemId: 88,
            groupDraft,
            IsNewGroupDraft: true,
            memberDraft);

        var resolution = snapshot.ResolveGroupSelection([AreaGroupSelectionKey.Create(1)]);

        Assert.Null(resolution.Selection);
        Assert.True(resolution.StoredSelectionFound);
        Assert.Equal(groupDraft, snapshot.GroupDraft);
        Assert.Equal(memberDraft, snapshot.MemberDraft);
    }

    [Fact]
    public void RestoresSelectedMemberOrFallsBackOnlyWhenStoredMemberWasDeleted()
    {
        var available = new long[] { 30, 20, 10 };

        Assert.Equal(20, Snapshot(AreaGroupSelectionKey.Create(1), selectedItemId: 20).ResolveItemSelection(available));
        Assert.Equal(30, Snapshot(AreaGroupSelectionKey.Create(1), selectedItemId: 99).ResolveItemSelection(available));
        Assert.Null(Snapshot(AreaGroupSelectionKey.Create(1), selectedItemId: null).ResolveItemSelection(available));
    }

    private static AreaGroupEditingSnapshot Snapshot(
        AreaGroupSelectionKey? selectedGroup,
        long? selectedItemId = null)
    {
        return new AreaGroupEditingSnapshot(
            selectedGroup,
            selectedItemId,
            new AreaGroupEditorDraft("名称", "范围", "说明", "重点", true, IsDirty: false),
            IsNewGroupDraft: false,
            new AreaGroupMemberDraftSnapshot(
                IsActive: false,
                Mode: "None",
                EditingItemId: null,
                TargetType: string.Empty,
                Building: string.Empty,
                FloorLabel: string.Empty,
                Note: string.Empty,
                SearchText: string.Empty,
                SubAreaText: string.Empty,
                CardName: string.Empty,
                SelectedTarget: null));
    }

    private static object BeginWatchLoad(AreaGroupEditingSession session, long? groupId, bool isCustom)
    {
        return Invoke(session, "BeginWatchLoad", groupId, isCustom)!;
    }

    private static bool TryMarkWatchReady(AreaGroupEditingSession session, object request)
    {
        return (bool)Invoke(session, "TryMarkWatchReady", request)!;
    }

    private static bool TryMarkWatchFailed(AreaGroupEditingSession session, object request)
    {
        return (bool)Invoke(session, "TryMarkWatchFailed", request)!;
    }

    private static bool IsWatchReady(AreaGroupEditingSession session, long groupId)
    {
        var method = session.GetType().GetMethod("IsWatchReady", [typeof(long)]);
        Assert.NotNull(method);
        return (bool)method.Invoke(session, [groupId])!;
    }

    private static bool TryCaptureReadyWatch(
        AreaGroupEditingSession session,
        long groupId,
        out object request)
    {
        var method = session.GetType().GetMethods()
            .SingleOrDefault(candidate =>
                candidate.Name == "TryCaptureReadyWatch" && candidate.GetParameters().Length == 2);
        Assert.NotNull(method);
        object?[] arguments = [groupId, null];
        var captured = (bool)method.Invoke(session, arguments)!;
        request = arguments[1]!;
        return captured;
    }

    private static bool IsWatchReady(AreaGroupEditingSession session, object request)
    {
        var method = session.GetType().GetMethods()
            .SingleOrDefault(candidate =>
                candidate.Name == "IsWatchReady" &&
                candidate.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType != typeof(long));
        Assert.NotNull(method);
        return (bool)method.Invoke(session, [request])!;
    }

    private static string WatchLoadState(AreaGroupEditingSession session)
    {
        return ReadProperty(session, "WatchLoadState")!.ToString()!;
    }

    private static long? CurrentWatchGroupId(AreaGroupEditingSession session)
    {
        return (long?)ReadProperty(session, "CurrentWatchGroupId");
    }

    private static long WatchLoadGeneration(AreaGroupEditingSession session)
    {
        return (long)ReadProperty(session, "WatchLoadGeneration")!;
    }

    private static long RequestGeneration(object request)
    {
        return (long)ReadProperty(request, "Generation")!;
    }

    private static object? Invoke(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethods()
            .SingleOrDefault(candidate =>
                candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length);
        Assert.NotNull(method);
        return method.Invoke(target, arguments);
    }

    private static object? ReadProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property.GetValue(target);
    }
}
