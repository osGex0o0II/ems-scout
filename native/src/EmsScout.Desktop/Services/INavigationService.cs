namespace EmsScout.Desktop.Services;

using EmsScout.Application.Devices;

public interface INavigationService
{
    void NavigateToData(DataNavigationRequest request);
}

public sealed record DataNavigationRequest(
    string SearchText = "",
    string Building = "",
    string CommunicationState = "",
    string AreaType = "",
    string Floor = "",
    string SubArea = "",
    string PageName = "",
    string Zuo = "")
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

    public void Attach(Action<DataNavigationRequest> navigateToData)
    {
        _navigateToData = navigateToData;
    }

    public void NavigateToData(DataNavigationRequest request)
    {
        _navigateToData?.Invoke(request);
    }
}
