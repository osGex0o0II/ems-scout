using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace EmsScout.Infrastructure.Exporting;

public sealed record SpreadsheetSheet(
    string Name,
    IReadOnlyList<IReadOnlyList<string>> Rows);

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
            throw new IOException($"Workbook already exists: {path}");
        }

        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create, Encoding.UTF8))
            {
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
                        WorksheetXml(sheets[index].Rows));
                }
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string WorksheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowIndex + 1}\">");
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var cell = CellReference(rowIndex + 1, columnIndex + 1);
                builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{EscapeXml(row[columnIndex])}</t></is></c>");
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData></worksheet>");
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
        return SecurityElement.Escape(SanitizeXml(value ?? string.Empty)) ?? string.Empty;
    }

    private static string SanitizeXml(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is '\t' or '\n' or '\r' ||
                current is >= '\u0020' and <= '\uD7FF' ||
                current is >= '\uE000' and <= '\uFFFD')
            {
                builder.Append(current);
            }
            else if (char.IsHighSurrogate(current) &&
                     index + 1 < value.Length &&
                     char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(current);
                builder.Append(value[++index]);
            }
            else
            {
                builder.Append('\uFFFD');
            }
        }

        return builder.ToString();
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
              <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
            </styleSheet>
            """;
    }
}
