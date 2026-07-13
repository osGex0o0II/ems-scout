using EmsScout.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmsScout.Desktop.Pages;

public sealed partial class DateManagementPage : Page
{
    private bool _syncingCalendar;

    public DateManagementViewModel ViewModel { get; }

    public DateManagementPage()
    {
        ViewModel = App.Services.GetRequiredService<DateManagementViewModel>();
        InitializeComponent();
        ViewModel.CalendarSelectionClearRequested += (_, _) => ClearCalendarSelection();
        ViewModel.CalendarRulesChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshCalendarMarkers);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PlanCalendar.MinDate = DateTimeOffset.Now.Date.AddYears(-2);
        PlanCalendar.MaxDate = DateTimeOffset.Now.Date.AddYears(5);
        PlanCalendar.SetDisplayDate(DateTimeOffset.Now);
        await ViewModel.LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void PlanCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (!_syncingCalendar) ViewModel.SetSelectedDates(sender.SelectedDates);
    }

    private void PlanCalendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Phase == 0) ApplyCalendarMarker(args.Item);
    }

    private void ApplyCalendarMarker(CalendarViewDayItem item)
    {
        if (ViewModel.IsNotOpenDate(item.Date))
        {
            item.SetDensityColors([Colors.Gray]);
        }
        else if (ViewModel.IsConfiguredDate(item.Date))
        {
            item.SetDensityColors([Colors.DodgerBlue]);
        }
        else
        {
            item.SetDensityColors([]);
        }
    }

    private void RefreshCalendarMarkers()
    {
        PlanCalendar.UpdateLayout();
        foreach (var dayItem in FindDescendants<CalendarViewDayItem>(PlanCalendar)) ApplyCalendarMarker(dayItem);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private void AddInterval_Click(object sender, RoutedEventArgs e) => ViewModel.AddInterval();

    private void RemoveInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleIntervalEditorRow row }) ViewModel.RemoveInterval(row);
    }

    private async void DeleteScheduleGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = ViewModel.SelectedScheduleGroup;
        if (group is null) return;
        if (await ConfirmAsync("删除计划组", $"将删除“{group.Name}”及其全部日期规则和适用对象。", "删除") == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteScheduleGroupAsync();
        }
    }

    private async void DeleteSelectedDates_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("删除日期规则", "将删除所选日期在当前计划组中的规则，区域组成员和设备数据不会删除。", "删除") == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveSelectedDatesAsync();
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearCalendarSelection();

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ScheduleRuleRow row } ||
            !DateTimeOffset.TryParse(row.Record.CalendarDate, out var date)) return;
        _syncingCalendar = true;
        PlanCalendar.SelectedDates.Clear();
        PlanCalendar.SelectedDates.Add(date);
        PlanCalendar.SetDisplayDate(date);
        _syncingCalendar = false;
        ViewModel.SelectRule(row);
    }

    private async void AddMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaGroupItemRow row }) await ViewModel.AddMemberAsync(row);
    }

    private async void RemoveMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleMemberRow row }) await ViewModel.RemoveMemberAsync(row);
    }

    private async void MarkNormal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleMemberRow row }) await ViewModel.SetMemberStatusAsync(row, "normal");
    }

    private async void MarkNotOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleMemberRow row }) await ViewModel.SetMemberStatusAsync(row, "not_open");
    }

    private void ClearCalendarSelection()
    {
        if (PlanCalendar is null) return;
        _syncingCalendar = true;
        PlanCalendar.SelectedDates.Clear();
        _syncingCalendar = false;
        ViewModel.SetSelectedDates([]);
    }

    private async Task<ContentDialogResult> ConfirmAsync(string title, string content, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync();
    }
}
