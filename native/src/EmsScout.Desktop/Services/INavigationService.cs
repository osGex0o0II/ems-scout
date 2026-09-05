namespace EmsScout.Desktop.Services;

using EmsScout.Application.Devices;

public interface INavigationService
{
    void NavigateToData(DataNavigationRequest request);

    void NavigateToGroups(long? groupId = null);
}

public sealed record DataNavigationRequest(
    string SearchText = "",
    string Building = "",
    string CommunicationState = "",
    string AreaType = "",
    string Floor = "",
    string SubArea = "",
    string PageName = "",
    string Zuo = "",
    long? AreaGroupId = null)
{
    public static DataNavigationRequest From(DeviceNavigationTarget target)
    {
        return new DataNavigationRequest(
            SearchText: target.SearchText,
            Building: target.Building,
            CommunicationState: string.Empty,
            AreaType: string.Empty);
    }
}

public sealed class NavigationService : INavigationService
{
    private Action<DataNavigationRequest>? _navigateToData;
    private Action<long?>? _navigateToGroups;

    public void Attach(
        Action<DataNavigationRequest> navigateToData,
        Action<long?> navigateToGroups)
    {
        _navigateToData = navigateToData;
        _navigateToGroups = navigateToGroups;
    }

    public void NavigateToData(DataNavigationRequest request)
    {
        _navigateToData?.Invoke(request);
    }

    public void NavigateToGroups(long? groupId = null)
    {
        _navigateToGroups?.Invoke(groupId);
    }
}
