using System.Reflection;
using EmsScout.Application.Updates;
using Windows.ApplicationModel;

namespace EmsScout.Desktop.Services;

public sealed class PackageAppVersionProvider : IAppVersionProvider
{
    public Version CurrentVersion
    {
        get
        {
            try
            {
                var version = Package.Current.Id.Version;
                return new Version(version.Major, version.Minor, version.Build, version.Revision);
            }
            catch (InvalidOperationException)
            {
                return Normalize(Assembly.GetEntryAssembly()?.GetName().Version);
            }
        }
    }

    private static Version Normalize(Version? version) => new(
        Math.Max(version?.Major ?? 0, 0),
        Math.Max(version?.Minor ?? 0, 0),
        Math.Max(version?.Build ?? 0, 0),
        Math.Max(version?.Revision ?? 0, 0));
}
