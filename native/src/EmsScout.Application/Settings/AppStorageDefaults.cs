namespace EmsScout.Application.Settings;

public static class AppStorageDefaults
{
    public static string ProductDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EMS Scout");

    public static string DataDirectory => Path.Combine(ProductDirectory, "data");

    public static string ExportDirectory => Path.Combine(ProductDirectory, "exports");
}
