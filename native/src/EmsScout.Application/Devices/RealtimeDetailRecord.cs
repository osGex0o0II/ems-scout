namespace EmsScout.Application.Devices;

public sealed record RealtimeDetailRecord(
    string RowId,
    string SourceFile,
    DateTimeOffset SourceUpdatedAt,
    string Building,
    double? Floor,
    string SubArea,
    string PageName,
    string Name,
    string DevId,
    string MeterId,
    string RtuId,
    int FieldCount,
    int RealtimeTagCount,
    int RealtimeValidTagCount,
    bool DefaultLike,
    string Error,
    string CardComm,
    string CardSwitch,
    string CardIndicator,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, bool> ValidFields)
{
    public string PowerState => Field("当前开关机状态");

    public string IndoorTemperature => Field("室内温度");

    public string SetTemperature => Field("设定温度");

    public string Fan => Field("设定风速");

    public string Mode => Field("系统模式设置");

    public string LockState => Field("集控锁定");

    public string ModbusAddress => Field("通讯地址 (Modbus)");

    public bool PointsComplete => RealtimeTagCount > 0 && RealtimeValidTagCount >= RealtimeTagCount;

    public bool IsInvalid => !string.IsNullOrWhiteSpace(Error) ||
                             DefaultLike ||
                             ValidFields.Any(item => item.Value == false);

    public string PointSummary => RealtimeTagCount <= 0
        ? "--"
        : $"{RealtimeValidTagCount}/{RealtimeTagCount}";

    public string Field(string name)
    {
        return Fields.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
