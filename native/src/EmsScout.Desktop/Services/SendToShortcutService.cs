namespace EmsScout.Desktop.Services;

public sealed class SendToShortcutService
{
    public const string ShortcutFileName = "EMS Scout.lnk";
    public const string AppUserModelId = "1FACE092-146B-4AE5-83DB-3990E6AE8371_ggf25w21tn4m2!App";

    public string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft",
        "Windows",
        "SendTo",
        ShortcutFileName);

    public void Apply(bool enabled)
    {
        var path = ShortcutPath;
        if (!enabled)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host could not be created.");
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        shortcut.Arguments = $"shell:AppsFolder\\{AppUserModelId}";
        shortcut.WorkingDirectory = AppContext.BaseDirectory;
        shortcut.Description = "EMS Scout Native WinUI 3";
        shortcut.Save();
    }
}
