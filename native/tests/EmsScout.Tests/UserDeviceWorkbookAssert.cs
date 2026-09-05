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
        "采集时间",
    ];

    public static void AssertShape(DeviceExportResult export)
    {
        Assert.NotEmpty(export.Sheets);
        Assert.Equal("全部设备", export.Sheets[0]);
        using var archive = ZipFile.OpenRead(export.Path);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        for (var index = 0; index < export.Sheets.Count; index++)
        {
            Assert.NotNull(archive.GetEntry($"xl/worksheets/sheet{index + 1}.xml"));
        }
        Assert.Null(archive.GetEntry($"xl/worksheets/sheet{export.Sheets.Count + 1}.xml"));

        var workbook = ReadEntry(archive, "xl/workbook.xml");
        foreach (var sheet in export.Sheets)
        {
            Assert.Contains($"name=\"{sheet}\"", workbook);
        }
        Assert.DoesNotContain("name=\"summary\"", workbook);
        Assert.DoesNotContain("name=\"filters\"", workbook);

        for (var index = 0; index < export.Sheets.Count; index++)
        {
            var rows = ReadRows(archive, index + 1);
            Assert.NotEmpty(rows);
            Assert.Equal(ExpectedHeader, rows[0]);
            Assert.All(rows, row => Assert.Equal(ExpectedHeader.Length, row.Count));
            var xml = ReadEntry(archive, $"xl/worksheets/sheet{index + 1}.xml");
            Assert.Contains("state=\"frozen\"", xml);
            Assert.Contains("<autoFilter", xml);
            Assert.Contains("<cols>", xml);
        }
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadRows(archive, 1);
    }

    public static IReadOnlyList<IReadOnlyList<string>> ReadRows(string path, int sheetNumber)
    {
        using var archive = ZipFile.OpenRead(path);
        return ReadRows(archive, sheetNumber);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadRows(ZipArchive archive, int sheetNumber)
    {
        var xml = ReadEntry(archive, $"xl/worksheets/sheet{sheetNumber}.xml");
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
