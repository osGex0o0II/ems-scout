using EmsScout.Application.Groups;
using EmsScout.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmsScout.Desktop.Pages;

public sealed partial class AreasPage : Page
{
    public GroupsViewModel ViewModel { get; }

    public AreasPage()
    {
        ViewModel = App.Services.GetRequiredService<GroupsViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void NavigateToAudit_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenAudit();
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = ViewModel.SelectedGroup;
        if (group?.GroupId is null ||
            !await ConfirmDestructiveActionAsync(
                "删除区域组",
                $"将永久删除“{group.Name}”的规则、正式成员、长期例外和待确认记录。",
                "删除区域组"))
        {
            return;
        }

        await ViewModel.DeleteGroupAsync();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaGroupRuleRecord rule })
        {
            if (!await ConfirmDestructiveActionAsync(
                    "删除持续规则",
                    $"删除“{rule.RuleTypeLabel} / {rule.ScopeLabel}”后，受影响的规则成员将在后续采集进入待确认移除。",
                    "删除规则"))
            {
                return;
            }

            await ViewModel.DeleteRuleAsync(rule);
        }
    }

    private async void DeleteManualMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaGroupMemberRecord member })
        {
            var effect = member.MemberOrigin == "rule"
                ? "该规则成员会被移除并加入长期屏蔽名单。"
                : "该成员会从区域组中移除。";
            if (!await ConfirmDestructiveActionAsync(
                    "移除分组成员",
                    $"{member.CardName} · {member.Building} {member.FloorLabel}\n\n{effect}",
                    "确认移除"))
            {
                return;
            }

            await ViewModel.DeleteManualMemberAsync(member);
        }
    }

    private async void DeleteException_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaGroupExceptionRecord exception })
        {
            if (!await ConfirmDestructiveActionAsync(
                    "撤销长期例外",
                    $"撤销“{exception.CardName}”的{exception.ExceptionTypeLabel}后，后续采集会重新按持续规则判断。",
                    "撤销例外"))
            {
                return;
            }

            await ViewModel.DeleteExceptionAsync(exception);
        }
    }

    private async Task<bool> ConfirmDestructiveActionAsync(
        string title,
        string content,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
