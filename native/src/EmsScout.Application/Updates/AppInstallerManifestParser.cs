using System.Xml;
using System.Xml.Linq;

namespace EmsScout.Application.Updates;

public static class AppInstallerManifestParser
{
    private const long MaxXmlCharacters = 256 * 1024;

    public static AppInstallerManifest Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new FormatException("App Installer manifest is empty.");
        }

        try
        {
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlCharacters,
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "AppInstaller", StringComparison.Ordinal))
            {
                throw new FormatException("AppInstaller root element is missing.");
            }

            var mainPackage = root.Elements()
                .SingleOrDefault(element => string.Equals(
                    element.Name.LocalName,
                    "MainPackage",
                    StringComparison.Ordinal));
            if (mainPackage is null)
            {
                throw new FormatException("MainPackage element is missing.");
            }

            var versionText = RequireAttribute(mainPackage, "Version");
            if (!Version.TryParse(versionText, out var version) ||
                version.Major < 0 ||
                version.Minor < 0 ||
                version.Build < 0 ||
                version.Revision < 0)
            {
                throw new FormatException("MainPackage Version must be a four-part version.");
            }

            return new AppInstallerManifest(
                ParseAbsoluteUri(RequireAttribute(root, "Uri"), "AppInstaller Uri"),
                RequireAttribute(mainPackage, "Name"),
                RequireAttribute(mainPackage, "Publisher"),
                version,
                ParseAbsoluteUri(RequireAttribute(mainPackage, "Uri"), "MainPackage Uri"));
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("App Installer manifest is invalid.", ex);
        }
    }

    private static string RequireAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"{element.Name.LocalName} {name} attribute is missing.");
        }

        return value;
    }

    private static Uri ParseAbsoluteUri(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"{fieldName} must be an absolute URI.");
        }

        return uri;
    }
}
