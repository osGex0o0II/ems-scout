using System.Text.Json;

namespace EmsScout.Application.Workflows;

public sealed record CollectionProgressPresentation(
    bool IsValid,
    bool IsEnumeration,
    string LogText,
    double? Percent,
    string ProgressMessage,
    string Building,
    int Current,
    int Total,
    int PageCards,
    int AccumulatedCards);

public static class CollectionProgressPresenter
{
    public static CollectionProgressPresentation Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var percent = ReadPercent(root);
            var message = ReadString(root, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return new(true, false, message, percent, message, string.Empty, 0, 0, 0, 0);
            }

            if (TryReadPositiveInt(root, "deviceTotal", out var deviceTotal))
            {
                var done = ReadInt(root, "deviceDone");
                var building = ReadString(root, "building");
                return new(
                    true,
                    false,
                    $"实时详情：{building} 设备 {done}/{deviceTotal}",
                    percent,
                    string.Empty,
                    building,
                    done,
                    deviceTotal,
                    0,
                    0);
            }

            var enumerationBuilding = ReadString(root, "bldg");
            var current = ReadInt(root, "curSa");
            var total = ReadInt(root, "totalSa");
            var cards = ReadInt(root, "cards");
            var accumulated = ReadInt(root, "acc");
            return new(
                true,
                true,
                $"采集进度：{enumerationBuilding} 子区 {current}/{total}，本页 {cards} 张，累计 {accumulated} 张",
                percent,
                string.Empty,
                enumerationBuilding,
                current,
                total,
                cards,
                accumulated);
        }
        catch (JsonException)
        {
            return new(false, false, "采集进度 " + json, null, string.Empty, string.Empty, 0, 0, 0, 0);
        }
    }

    private static double? ReadPercent(JsonElement root)
    {
        return root.TryGetProperty("percent", out var value) && value.TryGetDouble(out var percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    private static bool TryReadPositiveInt(JsonElement root, string name, out int value)
    {
        value = ReadInt(root, name);
        return value > 0;
    }

    private static int ReadInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }
}
