using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace EmsScout.Infrastructure.Exporting;

public sealed record SpreadsheetSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<double>? ColumnWidths = null);

public static class SpreadsheetWorkbookWriter
{
    public static void Write(string path, IReadOnlyList<SpreadsheetSheet> sheets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (sheets.Count == 0)
        {
            throw new ArgumentException("Workbook requires at least one worksheet.", nameof(sheets));
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create, Encoding.UTF8);
        AddTextEntry(archive, "[Content_Types].xml", ContentTypesXml(sheets.Count));
        AddTextEntry(archive, "_rels/.rels", RootRelationshipsXml());
        AddTextEntry(archive, "xl/workbook.xml", WorkbookXml(sheets));
        AddTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml(sheets.Count));
        AddTextEntry(archive, "xl/styles.xml", StylesXml());
        for (var index = 0; index < sheets.Count; index++)
        {
            AddTextEntry(
                archive,
                $"xl/worksheets/sheet{index + 1}.xml",
                WorksheetXml(sheets[index]));
        }
    }

    private static string WorksheetXml(SpreadsheetSheet sheet)
    {
        var rows = sheet.Rows;
        var lastColumn = CellReference(1, Math.Max(rows.Select(row => row.Count).DefaultIfEmpty(1).Max(), 1));
        var lastRow = Math.Max(rows.Count, 1);
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.Append(CultureInfo.InvariantCulture, $"<dimension ref=\"A1:{lastColumn[..^1]}{lastRow}\"/>");
        builder.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (sheet.ColumnWidths is { Count: > 0 })
        {
            builder.Append("<cols>");
            for (var index = 0; index < sheet.ColumnWidths.Count; index++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{sheet.ColumnWidths[index]}\" customWidth=\"1\"/>");
            }
            builder.Append("</cols>");
        }
        builder.Append("<sheetData>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowIndex + 1}\">");
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var cell = CellReference(rowIndex + 1, columnIndex + 1);
                var style = rowIndex == 0 ? " s=\"1\"" : string.Empty;
                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\"{style} t=\"inlineStr\"><is><t>{EscapeXml(row[columnIndex])}</t></is></c>");
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData>");
        if (rows.Count > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:{lastColumn[..^1]}{lastRow}\"/>");
        }
        builder.Append("</worksheet>");
        return builder.ToString();
    }

    private static string CellReference(int row, int column)
    {
        var value = column;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }

        return builder.Append(row).ToString();
    }

    private static string EscapeXml(string? value)
    {
        return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string ContentTypesXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            """);
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  <Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\n");
        }

        builder.Append("</Types>");
        return builder.ToString();
    }

    private static string RootRelationshipsXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """;
    }

    private static string WorkbookXml(IReadOnlyList<SpreadsheetSheet> sheets)
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
            """);
        for (var index = 0; index < sheets.Count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"    <sheet name=\"{EscapeXml(sheets[index].Name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>\n");
        }

        builder.Append(
            """
              </sheets>
            </workbook>
            """);
        return builder.ToString();
    }

    private static string WorkbookRelationshipsXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            """);
        for (var index = 1; index <= sheetCount; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"  <Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"  <Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\n");
        builder.Append("</Relationships>");
        return builder.ToString();
    }

    private static string StylesXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/><color rgb="FFFFFFFF"/></font></fonts>
              <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
            </styleSheet>
            """;
    }
}
