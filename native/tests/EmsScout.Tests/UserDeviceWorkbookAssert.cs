using System.IO.Compression;
using System.Xml.Linq;
using EmsScout.Application.Devices;

namespace EmsScout.Tests;

internal static class UserDeviceWorkbookAssert
{
    private static readonly string[] ExpectedHeader =
    [
        "楼栋",
        "座号",
        "楼层",
        "页面",
        "设备名",
        "区域",
        "开关机状态",
        "模式",
        "风速",
        "设置温度",
        "环境温度",
        "集控锁定状态",
    ];

    public static void AssertShape(DeviceExportResult export)
    {
        Assert.Equal(["devices"], export.Sheets);
        using var archive = ZipFile.OpenRead(export.Path);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        Assert.NotNull(archive.GetEntry("xl/worksheets/sheet1.xml"));
        Assert.Null(archive.GetEntry("xl/worksheets/sheet2.xml"));

        var workbook = ReadEntry(archive, "xl/workbook.xml");
        Assert.Contains("name=\"devices\"", workbook);
        Assert.DoesNotContain("name=\"summary\"", workbook);
        Assert.DoesNotContain("name=\"filters\"", workbook);

        var rows = ReadRows(archive);
        Assert.NotEmpty(rows);
        Assert.Equal(ExpectedHeader, rows[0]);
        Assert.Equal(export.RowCount + 1, rows.Count);
        Assert.All(rows, row => Assert.Equal(ExpectedHeader.Length, row.Count));
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadRows(archive);
    }

    public static IReadOnlyList<string?> ReadCellTypes(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var document = XDocument.Parse(ReadEntry(archive, "xl/worksheets/sheet1.xml"));
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document
            .Descendants(ns + "c")
            .Select(cell => (string?)cell.Attribute("t"))
            .ToArray();
    }

    public static string ReadWorksheetXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadEntry(archive, "xl/worksheets/sheet1.xml");
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(ZipArchive archive)
    {
        var xml = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        var document = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document
            .Descendants(ns + "row")
            .Select(row => (IReadOnlyList<string>)row
                .Elements(ns + "c")
                .Select(cell => cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? string.Empty)
                .ToArray())
            .ToArray();
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
