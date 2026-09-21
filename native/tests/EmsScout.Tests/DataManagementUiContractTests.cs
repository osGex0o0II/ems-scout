namespace EmsScout.Tests;

public sealed class DataManagementUiContractTests
{
    [Fact]
    public void CurrentDataSourceUsesCurrentInventoryInsteadOfHistorySnapshot()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataSourceOption.cs"));

        Assert.Contains("RunId = null", source);
        Assert.Contains("IsCurrent = true", source);
    }

    [Fact]
    public void DataPageUsesUserFacingFilterContractWithoutLegacyHints()
    {
        var root = LocateRepositoryRoot();
        var xamlPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("核验", xaml);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml);
        Assert.DoesNotContain("运行状态", xaml);
        Assert.DoesNotContain("打开上次导出", xaml);
        Assert.DoesNotContain("筛选和 Excel 导出使用同一组条件", xaml);
        Assert.Contains("ViewModel.DataStatusText", xaml);
        Assert.DoesNotContain("TextFillColorCautionBrush", xaml);
        Assert.Contains("SystemFillColorCautionBrush", xaml);
        Assert.DoesNotContain("Header=\"子区\"", xaml);
        Assert.DoesNotContain("Header=\"环境温度(℃)\"", xaml);
        Assert.DoesNotContain("Header=\"设置温度条件\"", xaml);
        Assert.DoesNotContain("Header=\"环境温度条件\"", xaml);
        Assert.DoesNotContain("Header=\"区域组\"", xaml);
        Assert.Contains("Header=\"开关机状态\"", xaml);
        Assert.Contains("ViewModel.AreaOptions", xaml);
        Assert.Contains("Header=\"设置温度(℃)\"", xaml);
        Assert.Contains("Header=\"页面\"", xaml);
        Assert.Contains("位置定位", xaml);
        Assert.Contains("状态筛选", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"仅适用于5号、6号楼\"", xaml);
        Assert.DoesNotContain("MaxHeight=\"260\"", xaml);
        Assert.DoesNotContain("<ScrollViewer VerticalScrollBarVisibility=\"Auto\">", xaml);
        Assert.Contains("Text=\"开关机状态\"", xaml);
        Assert.Contains("Text=\"集控锁定状态\"", xaml);
        Assert.Contains("Text=\"温度（设 / 环，℃）\"", xaml);
        Assert.DoesNotContain("Text=\"打开导出位置\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding Name}\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"{Binding PageName}\"", xaml);
        Assert.DoesNotContain("MinWidth=\"1620\"", xaml);
        Assert.Contains("x:Name=\"WideDataList\"", xaml);
        Assert.Contains("x:Name=\"CompactDataList\"", xaml);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1500\"", xaml);
        Assert.Contains("Text=\"楼栋\"", xaml);
        Assert.Contains("Text=\"集控锁定状态\"", xaml);

        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml.cs"));
        Assert.Contains("new FileSavePicker", codeBehind);
        Assert.Contains("PickSaveFileAsync", codeBehind);
        Assert.Contains("ViewModel.ExportAsync(file.Path, token)", codeBehind);
    }

    [Fact]
    public void DataPageUsesCompactTwoRowTableBelowWideLayoutBreakpoint()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));

        var compactStart = xaml.IndexOf("x:Name=\"CompactDataList\"", StringComparison.Ordinal);
        var wideStart = xaml.IndexOf("x:Name=\"WideDataList\"", StringComparison.Ordinal);

        Assert.True(compactStart >= 0);
        Assert.True(wideStart >= 0);
        Assert.Contains("AdaptiveTrigger MinWindowWidth=\"1500\"", xaml);
        Assert.Contains("Grid.Row=\"2\"", xaml[compactStart..]);
        Assert.Contains("Text=\"温度（设 / 环，℃）\"", xaml[compactStart..]);
        Assert.Contains("Text=\"温度（设 / 环，℃）\"", xaml[wideStart..]);
        Assert.DoesNotContain("MinWidth=\"1620\"", xaml);
    }

    [Fact]
    public void DataPageKeepsHeadersOnOneLineInsideBothTableLayouts()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var compactStart = xaml.IndexOf("x:Name=\"CompactDataList\"", StringComparison.Ordinal);
        var wideStart = xaml.IndexOf("x:Name=\"WideDataList\"", StringComparison.Ordinal);

        Assert.True(compactStart >= 0);
        Assert.True(wideStart > compactStart);

        foreach (var label in new[] { "温度（设 / 环，℃）", "集控锁定状态" })
        {
            AssertHeaderIsSingleLine(xaml[compactStart..wideStart], label);
            AssertHeaderIsSingleLine(xaml[wideStart..], label);
        }
    }

    [Fact]
    public void DataPageDeclaresStableRowsAndStretchingForBothTableLayouts()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var compactStart = xaml.IndexOf("x:Name=\"CompactDataList\"", StringComparison.Ordinal);
        var wideStart = xaml.IndexOf("x:Name=\"WideDataList\"", StringComparison.Ordinal);

        Assert.True(compactStart >= 0);
        Assert.True(wideStart > compactStart);

        var compact = xaml[compactStart..wideStart];
        var wide = xaml[wideStart..];

        Assert.DoesNotContain("<Grid.RowDefinitions>", compact);
        Assert.Contains("<Grid.RowDefinitions>", wide);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", compact);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", wide);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\" />", compact);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\" />", wide);
    }

    [Fact]
    public void DataPageKeepsHistoryControlTimestampOnlyInHeader()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.DoesNotContain("Text=\"历史批次\"", xaml);
        Assert.DoesNotContain("PlaceholderText=\"选择历史批次\"", xaml);
        Assert.DoesNotContain("CurrentDataSourceTimestamp", xaml);
        Assert.Contains("CurrentDataSourceTimestamp", viewModel);
        Assert.Contains("DisplayLabel => $\"{Label} · {Detail}\"", File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataSourceOption.cs")));
    }

    [Fact]
    public void DataPageBindsBatchPickerToRealCollectionWithScrollableDropDown()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));

        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.DataSources, Mode=OneWay}\"", xaml);
        Assert.Contains("<ComboBox.ItemTemplate>", xaml);
        Assert.Contains("Text=\"{Binding DisplayLabel}\"", xaml);
        Assert.DoesNotContain("Width=\"320\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml);
        Assert.Contains("MaxDropDownHeight=\"420\"", xaml);
    }

    [Fact]
    public void DataTablesUseComfortableCenteredColumns()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));

        Assert.DoesNotContain("MinWidth=\"1060\"", xaml);
        Assert.DoesNotContain("MinWidth=\"1460\"", xaml);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("DataTableCellTextStyle", xaml);
        Assert.Contains("<Setter Property=\"TextAlignment\" Value=\"Center\" />", xaml);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\" />", xaml);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"1.65*\" />", xaml);
    }

    [Fact]
    public void DataPageKeepsTableRowsReadableAndRemovesRedundantExportHeaderControls()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.DoesNotContain("OpenExportLocation_Click", xaml);
        Assert.DoesNotContain("打开导出位置", xaml);
        Assert.DoesNotContain("LastExportPath", xaml);
        Assert.DoesNotContain("Width=\"320\"", xaml);
        Assert.Contains("compact ? new Thickness(14, 14, 14, 14)", viewModel);
    }

    [Fact]
    public void DataPageCombinesTemperatureColumnsWithAColorCodedPair()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataDeviceRow.cs"));

        Assert.Contains("Text=\"温度（设 / 环，℃）\"", xaml);
        Assert.DoesNotContain("Text=\"设置温度(℃)\"", xaml);
        Assert.DoesNotContain("Text=\"环境温度(℃)\"", xaml);
        Assert.Contains("Text=\" / \"", xaml);
        Assert.Contains("Foreground=\"{Binding TemperatureForeground}\"", xaml);
        Assert.Contains("SetTemperatureValue", row);
        Assert.Contains("IndoorTemperatureValue", row);
        Assert.Contains("new Thickness(14, 12, 14, 12)", File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs")));
    }

    [Fact]
    public void DataPageUsesNeutralTextAndConditionalStatusColors()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataDeviceRow.cs"));

        Assert.DoesNotContain("Foreground=\"{ThemeResource AccentTextFillColorPrimaryBrush}\" Text=\"{Binding SetTemperatureValue}\"", xaml);
        Assert.DoesNotContain("Foreground=\"{ThemeResource SystemFillColorCautionBrush}\" Text=\"{Binding IndoorTemperatureValue}\"", xaml);
        Assert.Contains("Foreground=\"{Binding CommunicationForeground}\"", xaml);
        Assert.Contains("Foreground=\"{Binding TemperatureForeground}\"", xaml);
        Assert.Contains("TemperatureWarningThreshold", row);
        Assert.Contains("IsTemperatureWarning", row);
        Assert.Contains("record.CommunicationState != DeviceCommunicationState.Offline", row);
    }

    [Fact]
    public void OfflineCommunicationClearsRealtimeOnlySelections()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.Contains("string.Equals(value?.Value, \"离线\", StringComparison.OrdinalIgnoreCase)", viewModel);
        Assert.Contains("SelectedMode = ModeOptions.FirstOrDefault()", viewModel);
        Assert.Contains("SelectedFan = FanOptions.FirstOrDefault()", viewModel);
        Assert.Contains("SelectedSetTemperature = SetTemperatureOptions.FirstOrDefault()", viewModel);
        Assert.Contains("SelectedRealtimeLock = RealtimeLockOptions.FirstOrDefault()", viewModel);
    }

    [Fact]
    public void DataTableRowsUseTheLargerComfortableHeight()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));

        Assert.Contains("compact ? new Thickness(14, 14, 14, 14)", viewModel);
        Assert.Contains("new Thickness(14, 16, 14, 16)", viewModel);
    }

    [Fact]
    public void SettingsPageProvidesSecondarySectionsAndEditableAppearanceControls()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("<NavigationView", xaml);
        Assert.Contains("连接", xaml);
        Assert.Contains("数据表", xaml);
        Assert.Contains("SelectionChanged=\"SectionNavigation_SelectionChanged\"", xaml);
        Assert.DoesNotContain("<ColorPicker", xaml);
        Assert.Contains("OfflineStatusColorOptions", xaml);
        Assert.Contains("TemperatureWarningColorOptions", xaml);
        Assert.Contains("SelectedOfflineStatusColor", xaml);
        Assert.Contains("SelectedTemperatureWarningColor", xaml);
        Assert.Contains("TemperatureWarningThreshold", xaml);
        Assert.Contains("OfflineStatusColor", xaml);
        Assert.Contains("TemperatureWarningColor", xaml);
        Assert.Contains("SectionNavigation_SelectionChanged", codeBehind);
        Assert.Contains("TemperatureWarningThreshold", viewModel);
    }

    [Fact]
    public void SettingsPageUsesLimitedColorPresetsInsteadOfFreeFormColorEditing()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "SettingsViewModel.cs"));
        var option = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "ColorPresetOption.cs"));

        Assert.DoesNotContain("ColorPicker", xaml);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.OfflineStatusColorOptions, Mode=OneWay}\"", xaml);
        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.TemperatureWarningColorOptions, Mode=OneWay}\"", xaml);
        Assert.Contains("ColorPresetOption", viewModel);
        Assert.Contains("new(\"浅灰\"", viewModel);
        Assert.Contains("new(\"琥珀\"", viewModel);
        Assert.Contains("SwatchBrush", option);
    }

    [Fact]
    public void SettingsPageUsesAStableSecondaryPaneAndResponsiveContent()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));

        Assert.Contains("IsPaneToggleButtonVisible=\"False\"", xaml);
        Assert.Contains("PaneDisplayMode=\"Left\"", xaml);
        Assert.Contains("OpenPaneLength=\"176\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"860\"", xaml);
        Assert.Contains("x:Name=\"StatusTextBlock\"", xaml);
        Assert.DoesNotContain("x:Name=\"StatusBanner\"", xaml);
    }

    [Fact]
    public void FormInputsUsePurposeSizedMaximumWidthsAcrossPages()
    {
        var root = LocateRepositoryRoot();
        var settings = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "SettingsPage.xaml"));
        var data = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var areas = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var audit = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AuditPage.xaml"));
        var home = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "HomePage.xaml"));

        Assert.Contains("MaxWidth=\"640\"", settings);
        Assert.Contains("MaxWidth=\"260\"", settings);
        Assert.Contains("MaxWidth=\"220\"", data);
        Assert.Contains("MaxWidth=\"420\"", data);
        Assert.Contains("区域组规则", areas);
        Assert.Contains("关键词", areas);
        Assert.Contains("搜索楼层、页名、设备或问题依据", audit);
        Assert.Contains("MaxWidth=\"420\"", home);
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
    public void DataViewModelBuildsUnifiedAreaOptionsFromEnabledGroups()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);

        Assert.Contains("groupSet.Groups", source);
        Assert.Contains("var value = $\"group:{group.Id.ToString", source);
        Assert.DoesNotContain("SystemKey.Equals(\"public\"", source);
        Assert.DoesNotContain("SystemKey.Equals(\"non_public\"", source);
        Assert.Contains("$\"group:{group.Id.ToString", source);
        Assert.Equal(10, source.Split("DataFilterOption.All(\"全部\")", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("DataFilterOption.All(\"全部区域组\")", source);
        Assert.DoesNotContain("DataFilterOption.All(\"全部楼栋\")", source);
        Assert.DoesNotContain("DataFilterOption.All(\"全部开关机状态\")", source);
        Assert.DoesNotContain("DataFilterOption.All(\"全部设置温度\")", source);
        Assert.DoesNotContain("DataFilterOption.All(\"全部集控锁定状态\")", source);
        Assert.DoesNotContain("new DataFilterOption(\"无实时数据\", \"无实时数据\", -1)", source);
    }

    [Fact]
    public void DataViewModelTreatsSelectedNewestBatchAsLatest()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataViewModel.cs"));

        Assert.Contains("private long? _latestDataSourceRunId", source);
        Assert.Contains("_latestDataSourceRunId = latestOption?.RunId", source);
        Assert.Contains("SelectedDataSource.RunId == _latestDataSourceRunId", source);

        var loadingBlock = source[
            source.IndexOf("public bool IsLoading", StringComparison.Ordinal)..
            source.IndexOf("public bool CanMovePrevious", StringComparison.Ordinal)];
        Assert.Contains("OnPropertyChanged(nameof(CanChangeDataSource))", loadingBlock);
    }

    [Fact]
    public void DataViewModelDisplaysNewestBatchWhenNoBatchWasPreviouslySelected()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DataViewModel.cs"));

        Assert.Contains("var latestOption = DataSources.FirstOrDefault()", source);
        Assert.Contains("SelectedDataSource = selectedRunId is null", source);
        Assert.Contains("?? latestOption", source);
    }

    [Fact]
    public void CompactDataTableKeepsAllFieldsOnOneScrollableRow()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var compactStart = xaml.IndexOf("x:Name=\"CompactDataList\"", StringComparison.Ordinal);
        var wideStart = xaml.IndexOf("x:Name=\"WideDataList\"", StringComparison.Ordinal);

        Assert.True(compactStart >= 0);
        Assert.True(wideStart > compactStart);
        var compact = xaml[compactStart..wideStart];

        foreach (var label in new[] { "楼栋", "设备名", "集控锁定状态", "温度（设 / 环，℃）" })
        {
            Assert.Contains($"Text=\"{label}\"", compact);
        }

        Assert.Contains("Grid.Column=\"10\"", compact);
        var compactItemTemplate = compact[compact.IndexOf("<ListView.ItemTemplate>", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Grid.Row=\"1\"", compactItemTemplate);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", compact);
    }

    [Fact]
    public void DataManagementExposesVisibleAreaGroupFilterAndNavigationScope()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DataPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var navigation = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "INavigationService.cs"));

        Assert.DoesNotContain("Header=\"区域组\"", xaml);
        Assert.Contains("ViewModel.AreaOptions", xaml);
        Assert.Contains("ViewModel.SelectedArea", xaml);
        Assert.Contains("IAreaGroupRepository areaGroupRepository", viewModel);
        Assert.Contains("DataFilterOption.All(\"全部\")", viewModel);
        Assert.Contains("CaptureNavigationFilterLoadSnapshot(navigationRequest)", viewModel);
        Assert.Contains("MonitorGroupIds: areaFilter.MonitorGroupIds", viewModel);
        Assert.DoesNotContain("SelectedAreaGroup", viewModel);
        Assert.Contains("long? AreaGroupId = null", navigation);
    }

    [Fact]
    public void AreaGroupNavigationScopesFilterOptionsBeforeLoadingThem()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var initialize = source[
            source.IndexOf("public async Task InitializeAsync", StringComparison.Ordinal)..source.IndexOf("public async Task RefreshAsync", StringComparison.Ordinal)];
        var replaceAreaGroups = source[
            source.IndexOf("private IEnumerable<DataFilterOption> BuildAreaOptions", StringComparison.Ordinal)..];

        Assert.True(
            initialize.IndexOf("ApplyNavigationRequest(navigationRequest)", StringComparison.Ordinal) <
            initialize.IndexOf("CaptureNavigationFilterLoadSnapshot(navigationRequest)", StringComparison.Ordinal));
        Assert.True(
            initialize.IndexOf("CaptureNavigationFilterLoadSnapshot(navigationRequest)", StringComparison.Ordinal) <
            initialize.IndexOf("LoadFilterOptionsAndPageAsync(cancellationToken, navigationSnapshot)", StringComparison.Ordinal));
        Assert.Contains("group.Name", replaceAreaGroups);
    }

    [Fact]
    public void NavigationValuesSurviveTheFirstLoadBeforeFilterOptionsExist()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var initialize = source[
            source.IndexOf("public async Task InitializeAsync", StringComparison.Ordinal)..source.IndexOf("public async Task RefreshAsync", StringComparison.Ordinal)];
        var navigationSnapshot = source[
            source.IndexOf("private FilterLoadSnapshot CaptureNavigationFilterLoadSnapshot", StringComparison.Ordinal)..source.IndexOf("private bool IsCurrentFilterLoad", StringComparison.Ordinal)];
        var combinedLoad = source[
            source.IndexOf("private async Task<bool> LoadFilterOptionsAndPageAsync", StringComparison.Ordinal)..source.IndexOf("private FilterLoadSnapshot CaptureFilterLoadSnapshot", StringComparison.Ordinal)];

        Assert.Equal(1, initialize.Split("ApplyNavigationRequest(navigationRequest)", StringSplitOptions.None).Length - 1);
        Assert.Contains("var navigationSnapshot = navigationRequest is null", initialize);
        Assert.Contains("LoadFilterOptionsAndPageAsync(cancellationToken, navigationSnapshot)", initialize);
        Assert.Contains("areaValue = request.AreaGroupId is long groupId", navigationSnapshot);
        Assert.Contains("$\"group:{groupId.ToString(CultureInfo.InvariantCulture)}\"", navigationSnapshot);
        Assert.Contains("MonitorGroupIds: areaFilter.MonitorGroupIds", navigationSnapshot);
        Assert.Contains("Building: EmptyToNull(building)", navigationSnapshot);
        Assert.Contains("RunId: SelectedDataSource?.RunId", navigationSnapshot);
        Assert.Contains("requestedSnapshot ?? CaptureFilterLoadSnapshot()", combinedLoad);
        Assert.Contains("if (requestedSnapshot is null &&", combinedLoad);
        Assert.Contains("snapshot.SelectedArea", combinedLoad);
        Assert.DoesNotContain("new DataFilterOption(value, value, 0)", source);
    }

    [Fact]
    public void CombinedFilterLoadCapturesValuesBeforeAwaitAndOnlyLatestRequestApplies()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var combined = source[
            source.IndexOf("private async Task<bool> LoadFilterOptionsAndPageAsync", StringComparison.Ordinal)..source.IndexOf("private void ApplyFilterOptions", StringComparison.Ordinal)];
        var firstAwait = combined.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(combined.IndexOf("var snapshot = requestedSnapshot ?? CaptureFilterLoadSnapshot()", StringComparison.Ordinal) < firstAwait);
        Assert.True(combined.IndexOf("var requestVersion = Interlocked.Increment", StringComparison.Ordinal) < firstAwait);
        Assert.Contains("previousCancellation?.Cancel()", combined);
        Assert.Contains("IsCurrentFilterLoad(requestVersion, loadCancellation)", combined);
        Assert.Contains("snapshot.Query.Equals(BuildQuery", combined);
        Assert.Contains("_ = ApplyFiltersAsync(cancellationToken)", combined);
        Assert.Contains("snapshot.SelectedBuilding", combined);
    }

    [Fact]
    public void RapidBuildingAndFloorChangesSupersedeTheActiveFilterLoad()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var applyFilters = source[
            source.IndexOf("public async Task ApplyFiltersAsync", StringComparison.Ordinal)..source.IndexOf("private void SetDataError", StringComparison.Ordinal)];
        var selections = source[
            source.IndexOf("public async Task ApplyBuildingSelectionAsync", StringComparison.Ordinal)..source.IndexOf("public async Task MovePreviousAsync", StringComparison.Ordinal)];

        Assert.DoesNotContain("if (IsLoading)", applyFilters);
        Assert.DoesNotContain("IsLoading || _isInitializing", selections);
        Assert.Contains("_suppressFilterSelectionChanges", selections);
        Assert.Contains("LoadFilterOptionsAndPageAsync(cancellationToken)", applyFilters);
    }

    [Fact]
    public void ExportUsesOneCapturedQueryAndOneFullSnapshotWhenSupported()
    {
        var root = LocateRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs"));
        var export = source[
            source.IndexOf("public async Task ExportAsync", StringComparison.Ordinal)..source.IndexOf("public void ReportExportCanceled", StringComparison.Ordinal)];

        Assert.Contains("var exportQuery = BuildQuery(limit: ExportLimit, offset: 0)", export);
        Assert.Contains("repository.SearchAsync(exportQuery, exportCancellation.Token)", export);
        Assert.Contains("fullSnapshot with { Rows = fullSnapshot.Rows.Take(PageSize).ToArray() }", export);
        Assert.Contains("IDeviceSnapshotExportService snapshotExportService", export);
        Assert.Contains("ExportSnapshotToFileAsync(exportQuery, fullSnapshot, outputPath", export);
        Assert.Contains("IsCurrentFilterLoad(requestVersion, exportCancellation)", export);
        Assert.Contains("Interlocked.Exchange(ref _filterLoadCancellation, exportCancellation)", export);
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
        Assert.Contains("var areaFilter = BuildAreaQuery(SelectedArea?.Value)", buildQuery);
        Assert.Contains("MonitorGroupIds: areaFilter.MonitorGroupIds", buildQuery);
        Assert.Contains("AreaType: areaFilter.AreaType", buildQuery);
        Assert.Contains("Limit: limit", buildQuery);
        Assert.Contains("Offset: offset", buildQuery);
        Assert.Contains("RunId: SelectedDataSource?.RunId", buildQuery);
    }

    [Fact]
    public void DataViewModelRefreshesFilterOptionCountsWhenApplyingFilters()
    {
        var root = LocateRepositoryRoot();
        var viewModelPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DataViewModel.cs");
        var source = File.ReadAllText(viewModelPath);
        var applyFilters = source[
            source.IndexOf("public async Task ApplyFiltersAsync", StringComparison.Ordinal)..source.IndexOf("public async Task MovePreviousAsync", StringComparison.Ordinal)];

        Assert.Contains("LoadFilterOptionsAndPageAsync(cancellationToken)", applyFilters);
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
            viewModel.IndexOf("public async Task ApplyBuildingSelectionAsync", StringComparison.Ordinal)..viewModel.IndexOf("public async Task ApplyFloorSelectionAsync", StringComparison.Ordinal)];
        Assert.Contains("SelectedZuo = ZuoOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("SelectedFloor = FloorOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("SelectedPageName = PageNameOptions.FirstOrDefault()", buildingSelection);
        Assert.Contains("ApplyFiltersAsync(cancellationToken)", buildingSelection);

        var floorSelection = viewModel[
            viewModel.IndexOf("public async Task ApplyFloorSelectionAsync", StringComparison.Ordinal)..viewModel.IndexOf("public async Task MovePreviousAsync", StringComparison.Ordinal)];
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
        Assert.DoesNotContain("ReplaceAreaGroupOptions", source);
        Assert.DoesNotContain("SelectedAreaGroup", source);
        Assert.Contains("BuildAreaOptions", source);
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
    public void DeviceRecordRejectsInvalidOrOlderRealtimeLockValues()
    {
        var collectedAt = DateTimeOffset.Parse("2026-07-12T02:00:00Z");
        var validCurrent = DeviceWithRealtimeLock("开启") with
        {
            CollectedAt = collectedAt,
            Realtime = Realtime("开启", collectedAt.AddMinutes(1), valid: true),
        };
        var invalid = validCurrent with
        {
            Realtime = Realtime("关闭", collectedAt.AddMinutes(1), valid: false),
        };
        var stale = validCurrent with
        {
            Realtime = Realtime("关闭", collectedAt.AddDays(-1), valid: true),
        };

        Assert.Equal("开启", validCurrent.RealtimeLockText);
        Assert.Equal("未知", invalid.RealtimeLockText);
        Assert.Equal("关闭", stale.RealtimeLockText);
        Assert.False(invalid.RealtimeLocked);
        Assert.False(stale.RealtimeLocked);
    }

    [Fact]
    public void DeviceRecordKeepsUnmappedRealtimeLockAsUnknownWhileRetainingRawValue()
    {
        var record = DeviceWithRealtimeLock("32896") with
        {
            Realtime = Realtime("32896", valid: true, rawLockState: "32896"),
        };

        Assert.Equal("未知", record.RealtimeLockText);
        Assert.Equal("32896", record.Realtime?.RawLockState);
        Assert.False(record.RealtimeLocked);
    }

    [Fact]
    public void DevicePageNameFormatterUsesUserFacingPageLabels()
    {
        Assert.Equal("默认页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("default"));
        Assert.Equal("第1页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("一页"));
        Assert.Equal("第6页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("六页"));
        Assert.Equal("第2页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("裙楼/二页"));
        Assert.Equal("第1页", EmsScout.Application.Devices.DevicePageNameFormatter.Format("塔楼/一页"));
        Assert.Equal(
            EmsScout.Application.Devices.DevicePageNameFormatter.SortValue("一页"),
            EmsScout.Application.Devices.DevicePageNameFormatter.SortValue("裙楼/一页"));
        Assert.Equal(
            EmsScout.Application.Devices.DevicePageNameFormatter.SortValue("一页"),
            EmsScout.Application.Devices.DevicePageNameFormatter.SortValue("塔楼/一页"));
        Assert.Equal("BM", EmsScout.Application.Devices.DevicePageNameFormatter.Format("BM"));
    }

    [Fact]
    public void QuerySpecificationTreatsPrefixedPageNamesAsTheSamePage()
    {
        foreach (var storedPageName in new[] { "裙楼/一页", "塔楼/一页" })
        {
            var row = DeviceWithRealtimeLock("") with { PageName = storedPageName };

            Assert.True(EmsScout.Application.Devices.DeviceQuerySpecification.MatchesResult(
                row,
                new EmsScout.Application.Devices.DeviceQuery(PageName: "一页")));
        }
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

    private static EmsScout.Application.Devices.RealtimeDetailRecord Realtime(
        string lockState,
        DateTimeOffset? sourceUpdatedAt = null,
        bool valid = true,
        string? rawLockState = null)
    {
        return new EmsScout.Application.Devices.RealtimeDetailRecord(
            RowId: "rt-1",
            SourceFile: "test",
            SourceUpdatedAt: sourceUpdatedAt ?? DateTimeOffset.UnixEpoch,
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
                ["集控锁定"] = valid,
            },
            RawFields: rawLockState is null
                ? null
                : new Dictionary<string, string> { ["集控锁定"] = rawLockState });
    }

    private static int IndexOf(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing expected text: " + value);
        return index;
    }

    private static void AssertHeaderIsSingleLine(string tableMarkup, string label)
    {
        var line = tableMarkup
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .FirstOrDefault(candidate => candidate.Contains($"Text=\"{label}\"", StringComparison.Ordinal));

        Assert.NotNull(line);
        Assert.True(
            line.Contains("TextWrapping=\"NoWrap\"", StringComparison.Ordinal) ||
            tableMarkup.Contains("<Setter Property=\"TextWrapping\" Value=\"NoWrap\" />", StringComparison.Ordinal),
            "Header must use single-line text wrapping.");
        Assert.True(
            line.Contains("TextTrimming=\"CharacterEllipsis\"", StringComparison.Ordinal) ||
            tableMarkup.Contains("<Setter Property=\"TextTrimming\" Value=\"CharacterEllipsis\" />", StringComparison.Ordinal),
            "Header must use ellipsis trimming.");
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
