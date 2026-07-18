namespace EmsScout.Desktop.Services;

using EmsScout.Application.Devices;

public interface INavigationService
{
    void NavigateToData(DataNavigationRequest request);

    void NavigateToAudit(long? areaGroupId = null);
}

public sealed record AuditNavigationRequest(long? AreaGroupId = null);

public sealed record DataNavigationRequest(
    string SearchText = "",
    string Building = "",
    string CommunicationState = "",
    string AreaType = "",
    string Floor = "",
    string SubArea = "",
    string PageName = "",
    string DeviceUid = "",
    long? CardId = null,
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
    private Action<AuditNavigationRequest>? _navigateToAudit;

    public void Attach(Action<DataNavigationRequest> navigateToData, Action<AuditNavigationRequest> navigateToAudit)
    {
        _navigateToData = navigateToData;
        _navigateToAudit = navigateToAudit;
    }

    public void NavigateToData(DataNavigationRequest request)
    {
        _navigateToData?.Invoke(request);
    }

    public void NavigateToAudit(long? areaGroupId = null)
    {
        _navigateToAudit?.Invoke(new AuditNavigationRequest(areaGroupId));
    }
}
