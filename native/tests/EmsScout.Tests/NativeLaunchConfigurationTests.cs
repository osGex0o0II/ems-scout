using System.Xml.Linq;

namespace EmsScout.Tests;

public sealed class NativeLaunchConfigurationTests
{
    [Fact]
    public void DebugPackagedLaunchIsSelfContained()
    {
        var root = LocateRepositoryRoot();
        var projectPath = Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "EmsScout.Desktop.csproj");
        var project = XDocument.Load(projectPath);
        var debugGroup = project.Root!
            .Elements("PropertyGroup")
            .Single(element => string.Equals(
                (string?)element.Attribute("Condition"),
                "'$(Configuration)' == 'Debug'",
                StringComparison.Ordinal));

        Assert.Equal(
            "true",
            debugGroup.Element("SelfContained")?.Value.ToLowerInvariant());
    }

    [Fact]
    public void NativeRunInitializesSdkEnvironmentAndPropagatesFailure()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-native.ps1"));
        var invocation = script.IndexOf("& dotnet @args", StringComparison.Ordinal);
        var exitCheck = script.IndexOf("if ($LASTEXITCODE -ne 0)", StringComparison.Ordinal);

        Assert.Contains("windows-sdk-environment.ps1", script);
        Assert.Contains("Initialize-WindowsSdkEnvironment", script);
        Assert.True(invocation >= 0, "run-native.ps1 must invoke dotnet run.");
        Assert.True(exitCheck > invocation, "run-native.ps1 must check the launch exit code.");
        Assert.Contains("Native application launch failed with exit code", script);
    }

    [Fact]
    public void NativeRunProvidesAnIsolatedUiValidationMode()
    {
        var root = LocateRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "run-native.ps1"));
        var app = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "App.xaml.cs"));

        Assert.Contains("[switch]$UiValidation", script);
        Assert.Contains("ems-scout-ui-validation-", script);
        Assert.Contains("WinAppLaunchArgs", script);
        Assert.Contains("--ui-validation-settings-base64=", script);
        Assert.Contains("UI_VALIDATION_DIRECTORY=", script);
        Assert.Contains("ConvertTo-Json", script);
        Assert.Contains("AppInstance.GetCurrent().GetActivatedEventArgs()", app);
        Assert.Contains("AppLaunchOptions.Parse(desktopLaunchArguments)", app);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate the EMS Scout repository root.");
    }
}
