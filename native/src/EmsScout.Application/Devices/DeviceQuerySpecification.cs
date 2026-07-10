using EmsScout.Domain;

namespace EmsScout.Application.Devices;

public static class DeviceQuerySpecification
{
    public static bool MatchesScope(DeviceRecord row, DeviceQuery query)
    {
        return MatchesBuilding(row, query.Building) &&
               MatchesCommunication(row, query.CommunicationState) &&
               MatchesFloor(row, query.Floor) &&
               MatchesSubArea(row, query.SubArea) &&
               MatchesPageName(row, query.PageName) &&
               MatchesDeviceName(row, query.DeviceName) &&
               MatchesZuo(row, query.Zuo) &&
               MatchesTag(row, query.Tag) &&
               MatchesRealtimeMatch(row, query.RealtimeMatch) &&
               MatchesRealtimePoints(row, query.RealtimePoints) &&
               MatchesWatch(row, query.WatchState) &&
               MatchesSearch(row, query.SearchText);
    }

    public static bool MatchesResult(DeviceRecord row, DeviceQuery query)
    {
        return MatchesScope(row, query) &&
               MatchesArea(row, query.AreaType) &&
               MatchesExactText(row.Mode, query.Mode) &&
               MatchesExactText(row.Fan, query.Fan) &&
               MatchesExactText(row.SetTemperature, query.SetTemperature) &&
               MatchesExactText(row.IndoorTemperature, query.IndoorTemperature) &&
               MatchesRealtimeField(row, query.RealtimePower, detail => detail.PowerState) &&
               MatchesRealtimeField(row, query.RealtimeMode, detail => detail.Mode) &&
               MatchesRealtimeField(row, query.RealtimeFan, detail => detail.Fan) &&
               MatchesRealtimeLock(row, query.RealtimeLock) &&
               MatchesRealtimeField(row, query.RealtimeSystemType, detail => detail.Field("系统类型")) &&
               MatchesRealtimeText(row, query.RealtimeModbus, detail => detail.ModbusAddress) &&
               DeviceHealthRules.MatchesQuickFilter(row, query.QuickFilter);
    }

    private static bool MatchesBuilding(DeviceRecord row, string? building)
    {
        return string.IsNullOrWhiteSpace(building) ||
               string.Equals(row.Building, building.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesCommunication(DeviceRecord row, string? communicationState)
    {
        if (string.IsNullOrWhiteSpace(communicationState))
        {
            return true;
        }

        var expected = communicationState.Trim();
        return string.Equals(row.CommunicationStatusText, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFloor(DeviceRecord row, string? floor)
    {
        var allowed = ValueList(floor)
            .Select(DeviceFloorLabelFormatter.Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed.Count == 0 || allowed.Contains(DeviceFloorLabelFormatter.Normalize(row.FloorLabel));
    }

    private static bool MatchesZuo(DeviceRecord row, string? zuo)
    {
        var allowed = ValueList(zuo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed.Count == 0 || allowed.Contains(row.Zuo ?? string.Empty);
    }

    private static bool MatchesSubArea(DeviceRecord row, string? subArea)
    {
        return MatchesExactText(row.SubArea, subArea);
    }

    private static bool MatchesPageName(DeviceRecord row, string? pageName)
    {
        var allowed = ValueList(pageName)
            .Select(DevicePageNameFormatter.NormalizeValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed.Count == 0 || allowed.Contains(DevicePageNameFormatter.NormalizeValue(row.PageName));
    }

    private static bool MatchesDeviceName(DeviceRecord row, string? deviceName)
    {
        return string.IsNullOrWhiteSpace(deviceName) ||
               row.Name.Contains(deviceName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesExactText(string actual, string? expected)
    {
        var allowed = ValueList(expected).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowed.Count == 0 || allowed.Contains(actual ?? string.Empty);
    }

    private static bool MatchesTag(DeviceRecord row, string? tag)
    {
        return string.IsNullOrWhiteSpace(tag) ||
               row.TagList.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesRealtimeMatch(DeviceRecord row, string? realtimeMatch)
    {
        return realtimeMatch?.Trim() switch
        {
            null or "" or "all" => true,
            "matched" => row.Realtime is not null,
            "missing" => row.Realtime is null,
            "invalid" => row.Realtime?.IsInvalid == true,
            "manual" => row.HasManualOverride,
            "virtual" => row.IsVirtual,
            _ => true,
        };
    }

    private static bool MatchesRealtimePoints(DeviceRecord row, string? realtimePoints)
    {
        return realtimePoints?.Trim() switch
        {
            null or "" or "all" => true,
            "complete" => row.RealtimePointsComplete,
            "incomplete" => row.Realtime is null || !row.RealtimePointsComplete,
            "missing" => row.Realtime is null,
            _ => true,
        };
    }

    private static bool MatchesWatch(DeviceRecord row, string? watchState)
    {
        return watchState?.Trim() switch
        {
            null or "" or "all" => true,
            "watched" => row.IsWatched,
            "abnormal" => row.IsWatchAbnormal,
            "normal" => row.IsWatched && !row.IsWatchAbnormal,
            "unwatched" => !row.IsWatched,
            _ => true,
        };
    }

    private static bool MatchesArea(DeviceRecord row, string? areaType)
    {
        return areaType?.Trim() switch
        {
            null or "" or "all" => true,
            "public" or "公区" => row.AreaType == DeviceAreaClassifier.PublicArea,
            "private" or "非公区" => row.AreaType == DeviceAreaClassifier.PrivateArea,
            "unmatched" or "未匹配" => row.AreaType == "未匹配",
            _ => true,
        };
    }

    private static bool MatchesRealtimeField(
        DeviceRecord row,
        string? expected,
        Func<RealtimeDetailRecord, string> selector)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        if (string.Equals(expected.Trim(), "无实时数据", StringComparison.OrdinalIgnoreCase))
        {
            return row.Realtime is null;
        }

        return row.Realtime is not null &&
               string.Equals(selector(row.Realtime), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRealtimeLock(DeviceRecord row, string? expected)
    {
        return string.IsNullOrWhiteSpace(expected) ||
               string.Equals(row.RealtimeLockText, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesRealtimeText(
        DeviceRecord row,
        string? expected,
        Func<RealtimeDetailRecord, string> selector)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return row.Realtime is not null &&
               selector(row.Realtime).Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearch(DeviceRecord row, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var q = searchText.Trim();
        return SearchValues(row).Any(value => value.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SearchValues(DeviceRecord row)
    {
        yield return row.Name;
        yield return row.FloorLabel;
        yield return row.SubArea;
        yield return row.PageName;
        yield return row.Mode;
        yield return row.Fan;
        yield return row.IndoorTemperature;
        yield return row.SetTemperature;
        yield return row.Zuo ?? string.Empty;
        yield return row.CommunicationStatusText;
        yield return row.SwitchState;
        yield return row.Note ?? string.Empty;
        yield return row.Realtime?.DevId ?? string.Empty;
        yield return row.Realtime?.ModbusAddress ?? string.Empty;
        yield return row.MatchOverrideNote ?? string.Empty;
        yield return row.WatchState.Summary;
        yield return row.WatchState.Evidence;
        foreach (var tag in row.TagList)
        {
            yield return tag;
        }
    }

    private static IEnumerable<string> ValueList(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

}
