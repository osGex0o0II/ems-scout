namespace EmsScout.Tests;

public sealed class GroupSettingsUiContractTests
{
    [Fact]
    public void AreaPageExposesGenericEditableRuleGroups()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupSummaryRow.cs"));
        var ruleRow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.Contains("区域组规则", xaml);
        Assert.Contains("区域组名", xaml);
        Assert.Contains("备注", xaml);
        Assert.Contains("MatchModeOptions", xaml);
        Assert.Contains("包含", ruleRow);
        Assert.Contains("不含", ruleRow);
        Assert.Contains("关键词", xaml);
        Assert.Contains("导入规则", xaml);
        Assert.Contains("导出规则", xaml);
        Assert.Contains("SaveConfigurationAsync", viewModel);
        Assert.Contains("DeleteRuleRowAsync", viewModel);
        Assert.Contains("ImportRulesAsync", viewModel);
        Assert.Contains("ExportRulesAsync", viewModel);
        Assert.Contains("GroupKey", row);
        Assert.Contains("DeleteGroup_Click", xaml);
        Assert.Contains("EditEnabled", xaml);
        Assert.Contains("RuleRowBuilding_SelectionChanged", xaml);
        Assert.Contains("DeleteRuleRow_Click", xaml);
        Assert.DoesNotContain("添加楼层或设备", xaml);
        Assert.DoesNotContain("更多组设置", xaml);
        Assert.DoesNotContain("不可编辑", row);
    }

    [Fact]
    public void DataNavigationUsesGenericGroupId()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var navigation = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Services", "INavigationService.cs"));

        Assert.Contains("new DataNavigationRequest(AreaGroupId: groupId)", viewModel);
        Assert.Contains("long? AreaGroupId", navigation);
    }

    [Fact]
    public void NewGroupDraftRemainsVisibleBeforeItHasBeenPersisted()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("_isCreatingGroup", viewModel);
        Assert.Contains("IsCreatingGroup || SelectedGroup is not null", viewModel);
        Assert.Contains("IsCreatingGroup = true", viewModel);
        Assert.Contains("OnPropertyChanged(nameof(GroupListEmptyVisibility))", viewModel);
        Assert.Contains("暂无区域组", xaml);
        Assert.Contains("GroupListEmptyVisibility", xaml);
    }

    [Fact]
    public void AreaRuleEditorUsesInternalGroupKeyAndSelectableFloors()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.DoesNotContain("Header=\"groupKey\"", xaml);
        Assert.Contains("FloorOptions", xaml);
        Assert.Contains("LoadFloorsAsync", viewModel);
        Assert.Contains("RefreshRuleOptionsAsync", viewModel);
        Assert.Contains("floorLabels.Add(\"BM\")", viewModel);
        Assert.Contains("ScrollViewer", xaml);
        Assert.Contains("Width=\"144\" MinWidth=\"132\"", xaml);
        Assert.Contains("RuleEditorRow", xaml);
        Assert.Contains("保存区域组后可查看设备", viewModel);
    }

    [Fact]
    public void AreaPageGuardsAsyncRefreshesAndSurfacesLoadFailures()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("_ruleOptionsVersion", viewModel);
        Assert.Contains("RuleOptionsErrorVisibility", xaml);
        Assert.Contains("LoadingStateVisibility", xaml);
        Assert.Contains("GroupListErrorVisibility", xaml);
        Assert.Contains("CanRefresh", viewModel);
        Assert.Contains("ExportRulesAsync", codeBehind);
        Assert.Contains("try", codeBehind);
    }

    [Fact]
    public void AreaPageSerializesDialogsDuringConcurrentAsyncFailures()
    {
        var root = LocateRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));

        Assert.Contains("_dialogGate", codeBehind);
        Assert.Contains("await _dialogGate.WaitAsync()", codeBehind);
        Assert.Contains("_dialogGate.Release()", codeBehind);
    }

    [Fact]
    public void AreaPageCancelsLoadsWhenNavigationChanges()
    {
        var root = LocateRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));

        Assert.Contains("_loadCts", codeBehind);
        Assert.Contains("ViewModel.LoadAsync(cancellationToken)", codeBehind);
        Assert.Contains("OnNavigatedFrom", codeBehind);
        Assert.Contains("_loadCts?.Cancel()", codeBehind);
    }

    [Fact]
    public void AreaPageKeepsRuleOptionsInStableOrderAndDisablesFileActionsWhileBusy()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("OrderBy(FloorSortValue)", viewModel);
        Assert.Contains("ThenBy", viewModel);
        Assert.Contains("CanRunFileOperation", xaml);
        Assert.Contains("_ruleOptionsVersion", viewModel);
    }

    [Fact]
    public void AreaPageDoesNotHideFloorLoadFailuresAsWholeBuilding()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var normalized = viewModel.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("RuleOptionsError = $\"楼层加载失败：{ex.Message}\";\n            return;", normalized);
        Assert.DoesNotContain("RuleOptionsError = $\"楼层加载失败：{ex.Message}\";\n            floors = [];", normalized);
    }

    [Fact]
    public void AreaPageLoadsRuleFloorsWithoutRunningDeviceFacetQueries()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = viewModel.IndexOf("public async Task RefreshRuleOptionsAsync", StringComparison.Ordinal);
        var end = viewModel.IndexOf("    public async Task DeleteRuleRowAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = viewModel[start..end];
        Assert.DoesNotContain("LoadFilterOptionsAsync", method);
        Assert.Contains("LoadFloorsAsync", method);
        Assert.Contains("catch (Exception ex)", method);
    }

    [Fact]
    public void AreaPageKeepsNestedOperationsBusyUntilTheOuterOperationCompletes()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("private int _busyDepth;", viewModel);
        Assert.Contains("if (Interlocked.Increment(ref _busyDepth) == 1)", viewModel);
        Assert.Contains("if (Interlocked.Decrement(ref _busyDepth) == 0)", viewModel);
    }

    [Fact]
    public void AreaPageLoadsRuleMatchCountsFromOneDeviceSnapshot()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var start = viewModel.IndexOf("private async Task RefreshRuleMatchCountsAsync", StringComparison.Ordinal);
        var end = viewModel.IndexOf("    private static double FloorSortValue", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = viewModel[start..end];
        Assert.Contains("new DeviceQuery(Limit: 50000)", method);
        Assert.DoesNotContain("foreach (var building in rows", method);
    }

    [Fact]
    public void AreaPageDoesNotStartRuleMatchRefreshBeforeGroupOptionsFinish()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var start = viewModel.IndexOf("public async Task SelectGroupAsync", StringComparison.Ordinal);
        var end = viewModel.IndexOf("    [RelayCommand", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = viewModel[start..end];
        Assert.Contains("_suppressRuleMatchRefresh = true", method);
        Assert.Contains("_suppressRuleMatchRefresh = previousSuppressRuleMatchRefresh", method);
    }

    [Fact]
    public void WholeBuildingFloorUsesDashInTheAreaEditor()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.DoesNotContain("整栋楼", xaml);
        Assert.DoesNotContain("整栋楼", viewModel);
        Assert.DoesNotContain("整栋楼", row);
        Assert.Contains("ConfigureFloorOptions", row);
        Assert.Contains("_floor = string.IsNullOrWhiteSpace(record.FloorLabel) ? \"-\" : record.FloorLabel", row);
    }

    [Fact]
    public void AreaEditorAvoidsFixedHorizontalOverflow()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.DoesNotContain("MinWidth=\"760\"", xaml);
        Assert.DoesNotContain("MinWidth=\"720\"", xaml);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", xaml);
    }

    [Fact]
    public void AreaEditorUsesWideSettingsPanelAndHorizontalMetadataRow()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Contains("ColumnDefinition Width=\"144\" MinWidth=\"132\"", xaml);
        Assert.Contains("x:Name=\"GroupSettingsRow\"", xaml);
        Assert.Contains("HorizontalContentAlignment=\"Left\"", xaml);
        Assert.Contains("ColumnDefinition Width=\"2*\" MinWidth=\"132\"", xaml);
        Assert.DoesNotContain("<CheckBox AutomationProperties.Name=\"启用区域组\"", xaml);
    }

    [Fact]
    public void UnselectedRuleFieldsUseDashAndEmptyKeywordsAreSupported()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));
        var rules = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Application", "Groups", "AreaGroupRules.cs"));

        Assert.Contains("MatchModeOptions", xaml);
        Assert.Contains("[\"-\", \"包含\", \"不含\"]", row);
        Assert.Contains("_matchMode = \"-\"", row);
        Assert.Contains("return normalized switch", rules);
        Assert.Contains("\"\" or \"-\" or \"include\" or \"包含\" => Include", rules);
        Assert.DoesNotContain("规则至少需要一个关键词", rules);
    }

    [Fact]
    public void NewGroupEditorProvidesAnExplicitCancelAction()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("取消新建", xaml);
        Assert.Contains("Command=\"{x:Bind ViewModel.CancelNewGroupCommand}\"", xaml);
        Assert.Contains("CancelNewGroup", viewModel);
    }

    [Fact]
    public void CancelingNewGroupRestoresThePreviouslySelectedGroup()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var newGroupStart = viewModel.IndexOf("private void NewGroup", StringComparison.Ordinal);
        var cancelStart = viewModel.IndexOf("private void CancelNewGroup", StringComparison.Ordinal);
        var deleteStart = viewModel.IndexOf("public async Task DeleteGroupAsync", cancelStart, StringComparison.Ordinal);

        Assert.True(newGroupStart >= 0 && cancelStart > newGroupStart && deleteStart > cancelStart);
        var newGroupMethod = viewModel[newGroupStart..cancelStart];
        var cancelMethod = viewModel[cancelStart..deleteStart];
        Assert.Contains("_selectedGroupBeforeNew = SelectedGroup", newGroupMethod);
        Assert.Contains("SelectedGroup = groupToRestore", cancelMethod);
        Assert.Contains("_selectedGroupBeforeNew = null", cancelMethod);
    }

    [Fact]
    public void RuleRowsExposePerRuleMatchCountsAndSettingsUseCompactActions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("MatchCount", row);
        Assert.Contains("CountMatches", viewModel);
        Assert.Contains("IDeviceReadRepository", viewModel);
        Assert.Contains("保存区域组", xaml);
        Assert.DoesNotContain("保存规则", xaml);
        Assert.Contains("x:Name=\"GroupSettingsRow\"", xaml);
    }

    [Fact]
    public void RuleEditorUsesOneEditableRowPerRuleWithOneGroupSaveAction()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));

        Assert.DoesNotContain("RuleDraftVisibility", xaml);
        Assert.DoesNotContain("SaveRuleCommand", xaml);
        Assert.DoesNotContain("CancelRuleEditCommand", xaml);
        Assert.Contains("Text=\"序号\"", xaml);
        Assert.Contains("Text=\"楼栋\"", xaml);
        Assert.Contains("Text=\"座号\"", xaml);
        Assert.Contains("Text=\"楼层\"", xaml);
        Assert.Contains("Text=\"匹配\"", xaml);
        Assert.Contains("Text=\"关键词\"", xaml);
        Assert.Contains("Content=\"×\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"删除规则\"", xaml);
        Assert.DoesNotContain("ConfirmAsync(\"删除规则\"", codeBehind);
        Assert.Contains("Text=\"{x:Bind RuleOrder}\"", xaml);
        Assert.Contains("TextBox", xaml);
        Assert.Contains("Keywords", xaml);
        Assert.Contains("DeleteRuleRow_Click", codeBehind);
        Assert.Contains("Rules.Add", viewModel);
        Assert.Contains("new AreaGroupRuleRow", viewModel);
        Assert.Contains("SaveConfigurationAsync", viewModel);
        Assert.Contains("SaveGroup", viewModel);
        Assert.Contains("public string Building", row);
        Assert.Contains("public string Zuo", row);
        Assert.Contains("public string Floor", row);
        Assert.Contains("public string MatchMode", row);
        Assert.Contains("public string Keywords", row);
        Assert.DoesNotContain("AutomationProperties.Name=\"保存规则\"", xaml);
        Assert.Equal(1, xaml.Split("Command=\"{x:Bind ViewModel.SaveGroupCommand}\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, xaml.Split("IsEnabled=\"{x:Bind ViewModel.CanEditSelectedGroup, Mode=OneWay}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("IsEnabled=\"{Binding ViewModel.CanEditSelectedGroup, ElementName=RootPage, Mode=OneWay}\"", xaml);
    }

    [Fact]
    public void AddingRuleAppendsAnIndependentDraftInsteadOfResettingGlobalFields()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var begin = viewModel.IndexOf("private void BeginAddRule", StringComparison.Ordinal);
        var end = viewModel.IndexOf("private", begin + 1, StringComparison.Ordinal);

        Assert.True(begin >= 0);
        Assert.True(end > begin);
        var method = viewModel[begin..end];
        Assert.Contains("Rules.Add", method);
        Assert.Contains("new AreaGroupRuleRow", method);
        Assert.DoesNotContain("ResetRuleDraft", method);
    }

    [Fact]
    public void RuleRowsRenumberAfterRemovalAndUseCenteredStableColumns()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.Contains("SetRuleOrder", row);
        Assert.Contains("RenumberRules", viewModel);
        Assert.Contains("SetRuleOrder(index + 1)", viewModel);
        Assert.Equal(4, xaml.Split("Width=\"80\" MinWidth=\"72\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(4, xaml.Split("Width=\"36\" MinWidth=\"32\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("HorizontalAlignment=\"Stretch\" TextAlignment=\"Center\"", xaml);
        Assert.DoesNotContain("Grid.Column=\"1\" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Left\"", xaml);
        Assert.DoesNotContain("Grid.Column=\"2\" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Left\"", xaml);
        Assert.DoesNotContain("Grid.Column=\"3\" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Left\"", xaml);
        Assert.DoesNotContain("Grid.Column=\"4\" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Left\"", xaml);
        Assert.Contains("HorizontalContentAlignment=\"Left\" PlaceholderText=\"关键词，用 | 分隔\"", xaml);
    }

    [Fact]
    public void AreaGroupsUseOneTransactionalSavePath()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("CanEditSelectedGroup", viewModel);
        Assert.Contains("SaveConfigurationAsync", viewModel);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanEditSelectedGroup, Mode=OneWay}\"", File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml")));
        Assert.Contains("Rules.Clear()", viewModel[viewModel.IndexOf("private void CancelNewGroup", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("await areaGroupRepository.DeleteRuleAsync(row.Id)", viewModel);
    }

    [Fact]
    public void EditorGuardsUnsavedDraftsAndVersionsRuleOptionRequestsPerRow()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("HasUnsavedChanges", viewModel);
        Assert.Contains("当前有未保存修改", viewModel);
        Assert.Contains("_ruleOptionsVersions", viewModel);
        Assert.Contains("row", viewModel[viewModel.IndexOf("public async Task RefreshRuleOptionsAsync", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void RuleRowsShowTheirCurrentMatchCount()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Contains("Text=\"{x:Bind MatchCountText, Mode=OneWay}\"", xaml);
    }

    [Fact]
    public void NewGroupCanAddRulesBeforeItsFirstSave()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("IsCreatingGroup || SelectedGroup is not null", viewModel);
        Assert.Contains("var groupId = SelectedGroup?.GroupId ?? 0", viewModel);
        Assert.Contains("new AreaGroupRuleRow(groupId, nextOrder)", viewModel);
    }

    [Fact]
    public void RuleEditorHasRoomAtTheMinimumWindowSize()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Equal(1, xaml.Split("Width=\"144\" MinWidth=\"132\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("HorizontalScrollMode=\"Enabled\"", xaml);
        Assert.Contains("x:Name=\"GroupSettingsActions\"", xaml);
        Assert.Contains("<Setter Property=\"Width\" Value=\"120\" />", File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml")));
    }

    [Fact]
    public void GroupDeletionDialogExplainsItsImpactWithLiveCountsAndWarnings()
    {
        var root = LocateRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("ItemCount", codeBehind);
        Assert.Contains("CoveredAreas", codeBehind);
        Assert.Contains("SystemFillColorCriticalBrush", codeBehind);
        Assert.Contains("SystemFillColorCautionBrush", codeBehind);
        Assert.Contains("HasUnsavedChanges", codeBehind);
        Assert.Contains("Count", codeBehind);
        Assert.Contains("DeleteGroupAsync", viewModel);
    }

    [Fact]
    public void GroupSelectionUsesAnAsyncUnsavedChangesGuardBeforeChangingTheEditor()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedGroup, Mode=OneWay}\"", xaml);
        Assert.Contains("SelectionChanged=\"GroupList_SelectionChanged\"", xaml);
        Assert.Contains("GroupList_SelectionChanged", codeBehind);
        Assert.Contains("ContentDialogResult.Secondary", codeBehind);
        Assert.Contains("保存", codeBehind);
        Assert.Contains("放弃", codeBehind);
        Assert.Contains("DiscardChanges", viewModel);
        Assert.Contains("SelectGroupAsync", viewModel);
    }

    [Fact]
    public void SaveCompletesWithAStableCleanDraftAfterAsyncRuleRefreshes()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));

        Assert.Contains("public async Task<bool> SaveGroupAsync", viewModel);
        Assert.Contains("return true", viewModel);
        Assert.Contains("return false", viewModel);
        Assert.Contains("await SelectGroupAsync(saved.Id)", viewModel);
        Assert.Contains("HasUnsavedChanges = false", viewModel);
        Assert.Contains("_suppressDraftDirty", viewModel);
    }

    [Fact]
    public void RefreshUsesAnIconAndAddRuleUsesTheQuietToolbarStyle()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Contains("AutomationProperties.Name=\"刷新\"", xaml);
        Assert.Contains("<SymbolIcon Symbol=\"Refresh\" />", xaml);
        Assert.DoesNotContain("<TextBlock Text=\"刷新\" />", xaml);
        Assert.Contains("AutomationProperties.Name=\"添加规则\"", xaml);
        Assert.Contains("Style=\"{StaticResource EditorActionButtonStyle}\"", xaml);
        Assert.DoesNotContain("AutomationProperties.Name=\"添加规则\" Command=\"{x:Bind ViewModel.BeginAddRuleCommand}\" IsEnabled=\"{x:Bind ViewModel.CanBeginAddRule, Mode=OneWay}\" Style=\"{StaticResource PrimaryToolbarButtonStyle}\"", xaml);
        var appResources = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml"));
        Assert.Contains("x:Key=\"QuietToolbarButtonStyle\"", appResources);
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", appResources);
    }

    [Fact]
    public void AreaEditorUsesCompactMinimumWindowColumns()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));

        Assert.Contains("<ColumnDefinition Width=\"144\" MinWidth=\"132\" />", xaml);
        Assert.DoesNotContain("<ColumnDefinition Width=\"176\" MinWidth=\"168\" />", xaml);
        Assert.DoesNotContain("<ColumnDefinition Width=\"3*\" MinWidth=\"220\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"2*\" MinWidth=\"132\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"36\" MinWidth=\"32\" />", xaml);
        var normalized = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GroupSettingsActions\"\n                                Grid.Row=\"1\"\n                                HorizontalAlignment=\"Right\"", normalized);
    }

    [Fact]
    public void RuleEditorUsesAlignedContainersReadableBuildingColumnAndFixedMatchArea()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var appResources = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml"));
        var ruleRow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.Contains("<ListView.ItemContainerStyle>", xaml);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", xaml);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\" />", xaml);
        Assert.Equal(4, xaml.Split("Width=\"80\" MinWidth=\"72\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, xaml.Split("<ColumnDefinition Width=\"92\" />", StringSplitOptions.None).Length - 1);
        Assert.Contains("x:Name=\"GroupSettingsActions\"\n                                Grid.Row=\"1\"\n                                HorizontalAlignment=\"Right\"", xaml.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("Style=\"{StaticResource SectionTitleStyle}\" Text=\"区域组规则\"", xaml);
        Assert.Contains("Style=\"{StaticResource EditorFieldHeaderStyle}\" Text=\"区域组名\"", xaml);
        Assert.Contains("Style=\"{StaticResource EditorFieldHeaderStyle}\" Text=\"备注\"", xaml);
        Assert.Contains("x:Key=\"EditorFieldHeaderStyle\"", appResources);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"16\" />", appResources);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"SemiBold\" />", appResources);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"{ThemeResource ControlStrokeColorDefaultBrush}\" />", appResources);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"1\" />", appResources);
        Assert.Contains("TextBox Height=\"36\" MinWidth=\"0\" HorizontalAlignment=\"Stretch\" HorizontalContentAlignment=\"Left\"", xaml);
        Assert.Contains("{MatchCount:N0}", ruleRow);
        Assert.Contains("命中 {MatchCount:N0} 台", ruleRow);
        Assert.Contains("排除 {MatchCount:N0} 台", ruleRow);
        Assert.DoesNotContain("MatchCount:D4", ruleRow);
    }

    [Fact]
    public void DirtyStateUsesAValueSnapshotAndIgnoresOptionRefreshBindingNoise()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "GroupsViewModel.cs"));
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.Contains("AreaGroupDraftSnapshot", viewModel);
        Assert.Contains("RecomputeDraftDirty", viewModel);
        Assert.Contains("_suppressDraftDirty", viewModel);
        Assert.Contains("if (value is null)", row);
        Assert.Contains("_savedDraft is null ||", viewModel);
    }

    [Fact]
    public void UnsavedDialogIncludesCurrentGroupContextAndSavedCounts()
    {
        var root = LocateRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml.cs"));

        Assert.Contains("当前区域组：", codeBehind);
        Assert.Contains("当前规则：", codeBehind);
        Assert.Contains("已保存覆盖设备：", codeBehind);
        Assert.Contains("已保存覆盖区域：", codeBehind);
        Assert.Contains("切换区域组前处理未保存修改", codeBehind);
    }

    [Fact]
    public void EditorActionsShareAStableCenteredButtonSize()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "AreasPage.xaml"));
        var appResources = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "App.xaml"));

        Assert.Contains("x:Key=\"EditorActionButtonStyle\"", appResources);
        Assert.Contains("<Setter Property=\"Width\" Value=\"120\" />", appResources);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Center\" />", appResources);
        Assert.Contains("Style=\"{StaticResource EditorActionButtonStyle}\"", xaml);
        Assert.Equal(2, xaml.Split("Style=\"{StaticResource EditorActionButtonStyle}\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void MatchCountUsesAnExplicitPlaceholderUntilTheCountIsKnown()
    {
        var root = LocateRepositoryRoot();
        var row = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "AreaGroupRuleRow.cs"));

        Assert.Contains("? (Record.IsExclude ? \"排除 -- 台\" : \"命中 -- 台\")", row);
        Assert.Contains("命中 {MatchCount:N0} 台", row);
        Assert.DoesNotContain("D4", row);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) && Directory.Exists(Path.Combine(directory.FullName, "native")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
