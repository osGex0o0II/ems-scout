namespace EmsScout.Tests;

public sealed class DataManagementUiContractTests
{
    [Fact]
    public void DataPageUsesUserFacingFilterContractWithoutLegacyHints()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("核验", xaml);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Disabled\"", xaml);
        Assert.DoesNotContain("运行状态", xaml);
        Assert.DoesNotContain("打开上次导出", xaml);
        Assert.Contains("筛选和 Excel 导出使用同一组条件", xaml);
        Assert.DoesNotContain("Header=\"子区\"", xaml);
        Assert.DoesNotContain("Header=\"环境温度(℃)\"", xaml);
        Assert.DoesNotContain("Header=\"设置温度条件\"", xaml);
        Assert.DoesNotContain("Header=\"环境温度条件\"", xaml);
        Assert.Contains("Header=\"开关机状态\"", xaml);
        Assert.Contains("Header=\"设置温度(℃)\"", xaml);
        Assert.Contains("Header=\"页面\"", xaml);
        Assert.Contains("位置定位", xaml);
        Assert.Contains("状态筛选", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"仅适用于5号、6号楼\"", xaml);
        Assert.DoesNotContain("MaxHeight=\"260\"", xaml);
        Assert.DoesNotContain("<ScrollViewer VerticalScrollBarVisibility=\"Auto\">", xaml);
        Assert.Contains("Text=\"开关机状态\"", xaml);
        Assert.Contains("Text=\"集控锁定状态\"", xaml);
        Assert.Contains("Text=\"设置温度(℃)\"", xaml);
        Assert.Contains("Text=\"环境温度(℃)\"", xaml);
        Assert.Contains("Text=\"打开导出位置\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding Name}\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding PageName}\"", xaml);
        Assert.Contains("MinWidth=\"1620\"", xaml);
    }

    [Fact]
    public void DataPageOrdersFiltersByUserWorkflow()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.True(IndexOf(xaml, "Header=\"楼栋\"") < IndexOf(xaml, "Header=\"座号\""));
        Assert.True(IndexOf(xaml, "Header=\"座号\"") < IndexOf(xaml, "Header=\"楼层\""));
        Assert.True(IndexOf(xaml, "Header=\"楼层\"") < IndexOf(xaml, "Header=\"页面\""));
        Assert.True(IndexOf(xaml, "Header=\"页面\"") < IndexOf(xaml, "Header=\"设备名\""));
        Assert.True(IndexOf(xaml, "Header=\"设备名\"") < IndexOf(xaml, "Header=\"区域\""));
        Assert.True(IndexOf(xaml, "Header=\"区域\"") < IndexOf(xaml, "Header=\"开关机状态\""));
        Assert.True(IndexOf(xaml, "Header=\"开关机状态\"") < IndexOf(xaml, "Header=\"模式\""));
        Assert.True(IndexOf(xaml, "Header=\"模式\"") < IndexOf(xaml, "Header=\"风速\""));
        Assert.True(IndexOf(xaml, "Header=\"风速\"") < IndexOf(xaml, "Header=\"设置温度(℃)\""));
        Assert.True(IndexOf(xaml, "Header=\"设置温度(℃)\"") < IndexOf(xaml, "Header=\"集控锁定状态\""));
    }

    [Fact]
    public void DataViewModelExposesUnmatchedAreaOption()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("new DataFilterOption(\"公区\", \"公区\", -1)", source);
        Assert.Contains("new DataFilterOption(\"非公区\", \"非公区\", -1)", source);
        Assert.Contains("new DataFilterOption(\"未匹配\", \"未匹配\", -1)", source);
        Assert.Contains("DataFilterOption.All(\"全部开关机状态\")", source);
        Assert.Contains("DataFilterOption.All(\"全部设置温度\")", source);
        Assert.Contains("DataFilterOption.All(\"全部集控锁定状态\")", source);
        Assert.DoesNotContain("new DataFilterOption(\"无实时数据\", \"无实时数据\", -1)", source);
    }

    [Fact]
    public void DataViewModelBuildQueryIncludesAllDataManagementFields()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var buildQuery = source[
            source.IndexOf("private DeviceQuery BuildQuery", StringComparison.Ordinal)..];

        Assert.Contains("BuildQuery(limit: PageSize", source);
        Assert.Contains("BuildQuery(limit: ExportLimit", source);
        Assert.Contains("Building: EmptyToNull(SelectedBuilding?.Value)", buildQuery);
        Assert.Contains("CommunicationState: EmptyToNull(SelectedCommunication?.Value)", buildQuery);
        Assert.Contains("Floor: EmptyToNull(SelectedFloor?.Value)", buildQuery);
        Assert.Contains("DeviceName: EmptyToNull(DeviceNameText)", buildQuery);
        Assert.Contains("Zuo: CanFilterByZuo ? EmptyToNull(SelectedZuo?.Value) : null", buildQuery);
        Assert.Contains("PageName: EmptyToNull(SelectedPageName?.Value)", buildQuery);
        Assert.Contains("Mode: EmptyToNull(SelectedMode?.Value)", buildQuery);
        Assert.Contains("Fan: EmptyToNull(SelectedFan?.Value)", buildQuery);
        Assert.Contains("SetTemperature: EmptyToNull(SelectedSetTemperature?.Value)", buildQuery);
        Assert.Contains("RealtimeLock: EmptyToNull(SelectedRealtimeLock?.Value)", buildQuery);
        Assert.Contains("AreaType: EmptyToNull(SelectedArea?.Value)", buildQuery);
        Assert.Contains("Limit: limit", buildQuery);
        Assert.Contains("Offset: offset", buildQuery);
        Assert.Contains("RunId: null", buildQuery);
    }

    [Fact]
    public void DataViewModelRefreshesFilterOptionCountsWhenApplyingFilters()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var applyFilters = source[
            source.IndexOf("public async Task ApplyFiltersAsync", StringComparison.Ordinal)..
            source.IndexOf("public async Task MovePreviousAsync", StringComparison.Ordinal)];

        Assert.Contains("ReloadFilterOptionsAsync(cancellationToken)", applyFilters);
        Assert.Contains("LoadPageCoreAsync(cancellationToken)", applyFilters);
    }

    [Fact]
    public void DataPageCascadesBuildingAndFloorSelections()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.Contains("SelectionChanged=\"BuildingFilter_SelectionChanged\"", xaml);
        Assert.Contains("SelectionChanged=\"FloorFilter_SelectionChanged\"", xaml);

        var buildingSelection = viewModel[
            viewModel.IndexOf("public async Task ApplyBuildingSelectionAsync", StringComparison.Ordinal)..
            viewModel.IndexOf("public async Task ApplyFloorSelectionAsync", StringComparison.Ordinal)];
        Assert.Contains("SelectedZuo = ZuoOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("SelectedFloor = FloorOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("SelectedPageName = PageNameOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("ApplyFiltersAsync(cancellationToken)", buildingSelection);

        var floorSelection = viewModel[
            viewModel.IndexOf("public async Task ApplyFloorSelectionAsync", StringComparison.Ordinal)..
            viewModel.IndexOf("public async Task MovePreviousAsync", StringComparison.Ordinal)];
        Assert.Contains("SelectedPageName = PageNameOptions.FirstOrDefault()", floorSelection);
        Assert.Contains("ApplyFiltersAsync(cancellationToken)", floorSelection);
    }

    [Fact]
    public void DataViewModelPreservesSelectedFilterOptionsThatCurrentlyHaveNoRows()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var replaceOptions = source[
            source.IndexOf("private static void ReplaceOptions", StringComparison.Ordinal)..];

        Assert.Contains("string selectedValue = \"\"", replaceOptions);
        Assert.Contains("new DataFilterOption(selectedValue, selectedValue, 0)", replaceOptions);
        Assert.Contains("ReplaceOptions(BuildingOptions", source);
        Assert.Contains("selectedBuilding", source);
        Assert.Contains("RealtimeLockOptions", source);
        Assert.Contains("selectedRealtimeLock", source);
    }

    [Fact]
    public void DataDeviceRowUsesChineseTemperatureUnit()
    {
        var root = LocateRepositoryRoot();
        var rowPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataDeviceRow.cs");
        var recordPath = Path.Combine(root, "native", "src", "EmsScout.Application", "Devices", "DeviceRecord.cs");
        var source = File.ReadAllText(rowPath);
        var recordSource = File.ReadAllText(recordPath);

        Assert.Contains(" ℃", source);
        Assert.DoesNotContain("\" C\"", source);
        Assert.Contains("无实时数据", recordSource);
    }

    [Fact]
    public void QuerySpecificationTreatsMissingRealtimeAsFilterableLockState()
    {
        var record = new EmsScout.Application.Devices.DeviceRecord(
            Id: 1,
            Building: "1号",
            Floor: 1,
            FloorLabel: "1F",
            SubArea: "1F A",
            X: null,
            Y: null,
            PageName: "default",
            Name: "1-0101-KT",
            Layout: "grid",
            SwitchState: "OFF",
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "25",
            Fan: "中",
            Indicator: "",
            CommunicationText: "关机",
            CommunicationState: EmsScout.Domain.DeviceCommunicationState.Stopped);

        Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            record,
            new EmsScout.Application.Devices.DeviceQuery(RealtimeLock: "无实时数据")));
        Assert.False(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            record,
            new EmsScout.Application.Devices.DeviceQuery(RealtimeLock: "开启")));
    }

    [Fact]
    public void QuerySpecificationSeparatesUnknownRealtimeLockFromMissingRealtime()
    {
        var unknownLock = DeviceWithRealtimeLock("");
        var missingRealtime = DeviceWithRealtimeLock(null);

        Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            unknownLock,
            new EmsScout.Application.Devices.DeviceQuery(RealtimeLock: "未知")));
        Assert.False(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            missingRealtime,
            new EmsScout.Application.Devices.DeviceQuery(RealtimeLock: "未知")));
        Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            missingRealtime,
            new EmsScout.Application.Devices.DeviceQuery(RealtimeLock: "无实时数据")));
    }

    [Fact]
    public void DeviceRecordNormalizesUserFacingStatusText()
    {
        var record = DeviceWithRealtimeLock("");

        Assert.Equal("未知", record.CommunicationStatusText);
        Assert.Equal("未知", record.RealtimeLockText);
        Assert.Equal("无实时数据", (record with { Realtime = null }).RealtimeLockText);
    }

    [Fact]
    public void DevicePageNameFormatterUsesUserFacingPageLabels()
    {
        Assert.Equal("默认页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("default"));
        Assert.Equal("第1页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("一页"));
        Assert.Equal("第6页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("六页"));
        Assert.Equal("裙楼 / 第2页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("裙楼/二页"));
        Assert.Equal("BM", EmsScout.Application.Devices.DevicePageNameFormatter.Format("BM"));
    }

    [Fact]
    public void QuerySpecificationSupportsPageNameWithoutChangingExactTemperatureFilter()
    {
        var normal = DeviceWithRealtimeLock("") with { SetTemperature = "25", IndoorTemperature = "26", PageName = "2" };

        Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            normal,
            new EmsScout.Application.Devices.DeviceQuery(SetTemperature: "25", IndoorTemperature: "26")));
        Assert.False(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            normal,
            new EmsScout.Application.Devices.DeviceQuery(IndoorTemperature: "27")));
        Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            normal,
            new EmsScout.Application.Devices.DeviceQuery(PageName: "2")));
        Assert.False(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
            normal,
            new EmsScout.Application.Devices.DeviceQuery(PageName: "3")));
    }

    private static EmsScout.Application.Devices.DeviceRecord DeviceWithRealtimeLock(string? realtimeLock)
    {
        return new EmsScout.Application.Devices.DeviceRecord(
            Id: 1,
            Building: "1号",
            Floor: 1,
            FloorLabel: "1F",
            SubArea: "1F A",
            X: null,
            Y: null,
            PageName: "default",
            Name: "1-0101-KT",
            Layout: "grid",
            SwitchState: "OFF",
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "25",
            Fan: "中",
            Indicator: "",
            CommunicationText: "",
            CommunicationState: EmsScout.Domain.DeviceCommunicationState.Unknown,
            Realtime: realtimeLock is null ? null : Realtime(realtimeLock));
    }

    private static EmsScout.Application.Devices.RealtimeDetailRecord Realtime(string lockState)
    {
        return new EmsScout.Application.Devices.RealtimeDetailRecord(
            RowId: "rt-1",
            SourceFile: "test",
            SourceUpdatedAt: DateTimeOffset.UnixEpoch,
            Building: "1号",
            Floor: 1,
            SubArea: "1F A",
            PageName: "default",
            Name: "1-0101-KT",
            DevId: "dev-1",
            MeterId: string.Empty,
            RtuId: string.Empty,
            FieldCount: 1,
            RealtimeTagCount: 1,
            RealtimeValidTagCount: 1,
            DefaultLike: false,
            Error: string.Empty,
            CardComm: string.Empty,
            CardSwitch: string.Empty,
            CardIndicator: string.Empty,
            Fields: new Dictionary<string, string>
            {
                ["集控锁定"] = lockState,
            },
            ValidFields: new Dictionary<string, bool>
            {
                ["集控锁定"] = true,
            });
    }

    private static int IndexOf(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing expected text: " + value);
        return index;
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
