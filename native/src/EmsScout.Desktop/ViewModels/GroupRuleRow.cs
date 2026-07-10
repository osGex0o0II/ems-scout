namespace EmsScout.Desktop.ViewModels;

public sealed class GroupRuleRow(string title, string description, string effect)
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public string Effect { get; } = effect;
}
