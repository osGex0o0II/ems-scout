using System.Globalization;
using EmsScout.Application.Devices;
using EmsScout.Domain;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmsScout.Desktop.ViewModels;

public sealed class DataDeviceRow(
    DeviceRecord record,
    double temperatureWarningThreshold = 2.0,
    string offlineStatusColor = "#808080",
    string temperatureWarningColor = "#D97706")
{
    public long Id { get; } = record.Id;

    public string Name { get; } = string.IsNullOrWhiteSpace(record.Name) ? "--" : record.Name;

    public string DeviceId { get; } = record.IsVirtual ? $"虚拟 #{Math.Abs(record.Id)}" : $"#{record.Id}";

    public string Building { get; } = record.Building;

    public string Location { get; } = record.Location;

    public string FloorLabel { get; } = record.FloorLabel;

    public string SubArea { get; } = record.SubArea;

    public string PageName { get; } = record.PageLabel;

    public string CommunicationText { get; } = record.CommunicationStatusText;

    public string AreaType { get; } = record.AreaGroupText;

    public string Zuo { get; } = string.IsNullOrWhiteSpace(record.Zuo) ? "--" : record.Zuo;

    public string HealthLabel { get; } = record.Health.Label;

    public string IssueSummary { get; } = record.Health.IssueSummary;

    public string RealtimeMatchLabel { get; } = record.RealtimeMatchLabel;

    public string MatchOverride { get; } = record.MatchOverrideId is null
        ? "--"
        : $"#{record.MatchOverrideId} {record.MatchOverrideAction}";

    public string MatchOverrideNote { get; } = string.IsNullOrWhiteSpace(record.MatchOverrideNote)
        ? "--"
        : record.MatchOverrideNote;

    public string RealtimeLock { get; } = record.RealtimeLockText;

    public string RealtimePointSummary { get; } = record.Realtime?.PointSummary ?? "--";

    public string RealtimePower { get; } = string.IsNullOrWhiteSpace(record.Realtime?.PowerState) ? "--" : record.Realtime.PowerState;

    public string RealtimeModbus { get; } = string.IsNullOrWhiteSpace(record.Realtime?.ModbusAddress) ? "--" : record.Realtime.ModbusAddress;

    public string DevId { get; } = string.IsNullOrWhiteSpace(record.Realtime?.DevId) ? "--" : record.Realtime.DevId;

    public string RealtimeName { get; } = string.IsNullOrWhiteSpace(record.Realtime?.Name) ? record.Name : record.Realtime.Name;

    public string SwitchState { get; } = string.IsNullOrWhiteSpace(record.SwitchState) ? "-" : record.SwitchState;

    public string Mode { get; } = string.IsNullOrWhiteSpace(record.Mode) ? "--" : record.Mode;

    public string Fan { get; } = string.IsNullOrWhiteSpace(record.Fan) ? "--" : record.Fan;

    public string IndoorTemperature { get; } = string.IsNullOrWhiteSpace(record.IndoorTemperature) ? "--" : $"{record.IndoorTemperature} ℃";

    public string SetTemperature { get; } = string.IsNullOrWhiteSpace(record.SetTemperature) ? "--" : $"{record.SetTemperature} ℃";

    public string IndoorTemperatureValue { get; } = string.IsNullOrWhiteSpace(record.IndoorTemperature) ? "--" : record.IndoorTemperature.Trim();

    public string SetTemperatureValue { get; } = string.IsNullOrWhiteSpace(record.SetTemperature) ? "--" : record.SetTemperature.Trim();

    public double TemperatureWarningThreshold { get; } = double.IsFinite(temperatureWarningThreshold)
        ? Math.Max(0, temperatureWarningThreshold)
        : 2.0;

    public double? TemperatureDifference { get; } = record.CommunicationState != DeviceCommunicationState.Offline
        ? ParseTemperatureDifference(record.IndoorTemperature, record.SetTemperature)
        : null;

    public bool IsTemperatureWarning => TemperatureDifference is double difference && difference > TemperatureWarningThreshold;

    public Brush? CommunicationForeground { get; } = record.CommunicationStatusText == "离线"
        ? CreateBrush(offlineStatusColor, Color.FromArgb(255, 128, 128, 128))
        : null;

    public Brush? TemperatureForeground => IsTemperatureWarning
        ? CreateBrush(temperatureWarningColor, Color.FromArgb(255, 217, 119, 6))
        : null;

    public string Indicator { get; } = string.IsNullOrWhiteSpace(record.Indicator) ? "--" : record.Indicator;

    public string Note { get; } = string.IsNullOrWhiteSpace(record.Note) ? "--" : record.Note;

    public string Tags { get; } = record.TagList.Count == 0 ? "--" : string.Join(", ", record.TagList);

    public string WatchLabel { get; } = record.WatchState.IsWatched
        ? record.WatchState.IsAbnormal ? "关注异常" : "关注正常"
        : "未关注";

    public string WatchSummary { get; } = record.WatchState.Summary;

    public string WatchEvidence { get; } = string.IsNullOrWhiteSpace(record.WatchState.Evidence)
        ? "--"
        : record.WatchState.Evidence;

    public string WatchWindow { get; } = string.IsNullOrWhiteSpace(record.WatchState.WindowText)
        ? "--"
        : record.WatchState.WindowText;

    public string RawNote { get; } = record.Note ?? string.Empty;

    public string RawTags { get; } = string.Join(", ", record.TagList);

    public bool HasRealtime => record.Realtime is not null;

    private static double? ParseTemperatureDifference(string? indoor, string? setTemperature)
    {
        if (!double.TryParse(indoor, NumberStyles.Float, CultureInfo.InvariantCulture, out var indoorValue) ||
            !double.TryParse(setTemperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var setValue))
        {
            return null;
        }

        return Math.Abs(indoorValue - setValue);
    }

    private static SolidColorBrush CreateBrush(string? value, Color fallback)
    {
        var candidate = value?.Trim();
        if (candidate is null || (candidate.Length != 7 && candidate.Length != 9) || candidate[0] != '#')
        {
            return new SolidColorBrush(fallback);
        }

        try
        {
            var offset = candidate.Length == 9 ? 1 : 0;
            var alpha = candidate.Length == 9 ? Convert.ToByte(candidate.Substring(1, 2), 16) : (byte)255;
            var red = Convert.ToByte(candidate.Substring(1 + offset, 2), 16);
            var green = Convert.ToByte(candidate.Substring(3 + offset, 2), 16);
            var blue = Convert.ToByte(candidate.Substring(5 + offset, 2), 16);
            return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(fallback);
        }
    }
}
