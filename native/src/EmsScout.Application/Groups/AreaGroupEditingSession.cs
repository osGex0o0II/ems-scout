namespace EmsScout.Application.Groups;

public sealed record AreaGroupSelectionKey(
    long? GroupId,
    string AreaFilter,
    string CommunicationFilter,
    string QuickFilter)
{
    public static AreaGroupSelectionKey Create(
        long? groupId,
        string? areaFilter = null,
        string? communicationFilter = null,
        string? quickFilter = null)
    {
        return new AreaGroupSelectionKey(
            groupId,
            Normalize(areaFilter),
            Normalize(communicationFilter),
            Normalize(quickFilter));
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed record AreaGroupEditorDraft(
    string Name,
    string AreaLabel,
    string Description,
    string Priority,
    bool Enabled,
    bool IsDirty);

public sealed record AreaGroupTargetSelection(
    string Type,
    string Building,
    string FloorLabel,
    string SubAreaText,
    string CardName,
    string DeviceUid = "",
    string PageName = "",
    string SourceKey = "",
    int Occurrence = 1);

public sealed record AreaGroupMemberDraftSnapshot(
    bool IsActive,
    string Mode,
    long? EditingItemId,
    string TargetType,
    string Building,
    string FloorLabel,
    string Note,
    string SearchText,
    string SubAreaText,
    string CardName,
    AreaGroupTargetSelection? SelectedTarget,
    string DeviceUid = "");

public sealed record AreaGroupSelectionResolution(
    AreaGroupSelectionKey? Selection,
    bool StoredSelectionFound);

public sealed record AreaGroupEditingSnapshot(
    AreaGroupSelectionKey? SelectedGroup,
    long? SelectedItemId,
    AreaGroupEditorDraft GroupDraft,
    bool IsNewGroupDraft,
    AreaGroupMemberDraftSnapshot MemberDraft)
{
    public bool HasPendingDraft => IsNewGroupDraft || GroupDraft.IsDirty || MemberDraft.IsActive;

    public AreaGroupSelectionResolution ResolveGroupSelection(IEnumerable<AreaGroupSelectionKey> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (IsNewGroupDraft)
        {
            return new AreaGroupSelectionResolution(null, true);
        }

        var choices = available.ToArray();
        if (SelectedGroup is not null)
        {
            var stored = choices.FirstOrDefault(candidate => SameSelection(candidate, SelectedGroup));
            if (stored is not null)
            {
                return new AreaGroupSelectionResolution(stored, true);
            }
        }

        return new AreaGroupSelectionResolution(choices.FirstOrDefault(), false);
    }

    private static bool SameSelection(AreaGroupSelectionKey candidate, AreaGroupSelectionKey stored)
    {
        if (stored.GroupId is not null)
        {
            return candidate.GroupId == stored.GroupId;
        }

        return candidate.GroupId is null &&
               candidate.AreaFilter == stored.AreaFilter &&
               candidate.CommunicationFilter == stored.CommunicationFilter &&
               candidate.QuickFilter == stored.QuickFilter;
    }

    public long? ResolveItemSelection(IEnumerable<long> availableItemIds)
    {
        ArgumentNullException.ThrowIfNull(availableItemIds);
        if (SelectedItemId is null)
        {
            return null;
        }

        var choices = availableItemIds.ToArray();
        return choices.Contains(SelectedItemId.Value)
            ? SelectedItemId
            : choices.Cast<long?>().FirstOrDefault();
    }
}

public sealed record AreaGroupMainLoadRequest(long Version, AreaGroupEditingSnapshot Snapshot);

public sealed record AreaGroupFloorLoadRequest(long Version, string Building);

public enum AreaGroupWatchLoadState
{
    NotApplicable,
    Loading,
    Ready,
    Failed,
}

public sealed record AreaGroupWatchLoadRequest(
    long Generation,
    long? GroupId,
    bool IsApplicable);

public sealed class AreaGroupEditingSession
{
    private readonly object _watchLoadLock = new();
    private long _mainVersion;
    private long _floorVersion;
    private long _watchLoadGeneration;
    private long? _currentWatchGroupId;
    private AreaGroupWatchLoadState _watchLoadState = AreaGroupWatchLoadState.NotApplicable;

    public AreaGroupWatchLoadState WatchLoadState
    {
        get
        {
            lock (_watchLoadLock)
            {
                return _watchLoadState;
            }
        }
    }

    public long WatchLoadGeneration
    {
        get
        {
            lock (_watchLoadLock)
            {
                return _watchLoadGeneration;
            }
        }
    }

    public long? CurrentWatchGroupId
    {
        get
        {
            lock (_watchLoadLock)
            {
                return _currentWatchGroupId;
            }
        }
    }

    public AreaGroupMainLoadRequest BeginMainLoad(AreaGroupEditingSnapshot snapshot)
    {
        return new AreaGroupMainLoadRequest(Interlocked.Increment(ref _mainVersion), snapshot);
    }

    public bool IsCurrentMain(AreaGroupMainLoadRequest request)
    {
        return request.Version == Interlocked.Read(ref _mainVersion);
    }

    public bool TryAcceptMain(AreaGroupMainLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsCurrentMain(request);
    }

    public bool TryRetainMainOnFailure(
        AreaGroupMainLoadRequest request,
        out AreaGroupEditingSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCurrentMain(request))
        {
            snapshot = null;
            return false;
        }

        snapshot = request.Snapshot;
        return true;
    }

    public AreaGroupFloorLoadRequest BeginFloorLoad(string building)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(building);
        return new AreaGroupFloorLoadRequest(Interlocked.Increment(ref _floorVersion), building.Trim());
    }

    public bool TryAcceptFloor(AreaGroupFloorLoadRequest request, string currentBuilding)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Version == Interlocked.Read(ref _floorVersion) &&
               string.Equals(request.Building, currentBuilding?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public AreaGroupWatchLoadRequest BeginWatchLoad(long? groupId, bool isCustom)
    {
        lock (_watchLoadLock)
        {
            _watchLoadGeneration++;
            _currentWatchGroupId = groupId;
            var isApplicable = isCustom && groupId is not null;
            _watchLoadState = isApplicable
                ? AreaGroupWatchLoadState.Loading
                : AreaGroupWatchLoadState.NotApplicable;
            return new AreaGroupWatchLoadRequest(_watchLoadGeneration, groupId, isApplicable);
        }
    }

    public bool IsCurrentWatch(AreaGroupWatchLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_watchLoadLock)
        {
            return IsCurrentWatchCore(request);
        }
    }

    public bool TryMarkWatchReady(AreaGroupWatchLoadRequest request)
    {
        return TryTransitionWatch(request, AreaGroupWatchLoadState.Ready);
    }

    public bool TryMarkWatchFailed(AreaGroupWatchLoadRequest request)
    {
        return TryTransitionWatch(request, AreaGroupWatchLoadState.Failed);
    }

    public bool IsWatchReady(long groupId)
    {
        lock (_watchLoadLock)
        {
            return _watchLoadState == AreaGroupWatchLoadState.Ready &&
                   _currentWatchGroupId == groupId;
        }
    }

    public bool TryCaptureReadyWatch(long groupId, out AreaGroupWatchLoadRequest? request)
    {
        lock (_watchLoadLock)
        {
            if (_watchLoadState != AreaGroupWatchLoadState.Ready ||
                _currentWatchGroupId != groupId)
            {
                request = null;
                return false;
            }

            request = new AreaGroupWatchLoadRequest(
                _watchLoadGeneration,
                _currentWatchGroupId,
                IsApplicable: true);
            return true;
        }
    }

    public bool IsWatchReady(AreaGroupWatchLoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_watchLoadLock)
        {
            return request.IsApplicable &&
                   _watchLoadState == AreaGroupWatchLoadState.Ready &&
                   IsCurrentWatchCore(request);
        }
    }

    private bool TryTransitionWatch(
        AreaGroupWatchLoadRequest request,
        AreaGroupWatchLoadState nextState)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_watchLoadLock)
        {
            if (!request.IsApplicable ||
                _watchLoadState != AreaGroupWatchLoadState.Loading ||
                !IsCurrentWatchCore(request))
            {
                return false;
            }

            _watchLoadState = nextState;
            return true;
        }
    }

    private bool IsCurrentWatchCore(AreaGroupWatchLoadRequest request)
    {
        return request.Generation == _watchLoadGeneration &&
               request.GroupId == _currentWatchGroupId;
    }
}
