using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Builds a single-sheet Excel (.xlsx) workbook from rows of text, so the model can
/// attach a spreadsheet by sending CSV text rather than base64-encoding a binary file
/// itself. Written directly against the Open XML package format to avoid pulling in a
/// spreadsheet library for one sheet of plain values.
///
/// The first row is treated as a bold, frozen header. Cells that are plain decimal
/// numbers are written as numbers; everything else (including values with leading
/// zeros such as order or phone numbers) is written as literal text, never as a formula.
/// </summary>
public static partial class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int MaxRows = 1_048_576;
    private const int MaxColumns = 16_384;
    private const int MaxCellLength = 32_767;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Parses <paramref name="csv"/> (comma- or tab-separated) and returns the workbook bytes.</summary>
    public static byte[] FromCsv(string csv) => Write(CsvParser.Parse(csv));

    public static byte[] Write(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count > MaxRows)
            throw new ArgumentException($"The spreadsheet has {rows.Count} rows; Excel allows at most {MaxRows}.");
        if (rows.Any(r => r.Count > MaxColumns))
            throw new ArgumentException($"A row has more than {MaxColumns} columns, the Excel maximum.");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypesXml);
            AddEntry(zip, "_rels/.rels", RootRelsXml);
            AddEntry(zip, "xl/workbook.xml", WorkbookXml);
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
            AddEntry(zip, "xl/styles.xml", StylesXml);

            var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var sheetStream = sheet.Open();
            WriteSheet(sheetStream, rows);
        }

        return stream.ToArray();
    }

    private static void WriteSheet(Stream output, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false) };
        using var xml = XmlWriter.Create(output, settings);

        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("worksheet", MainNs);

        if (rows.Count > 1)
        {
            // Freeze the header row so it stays visible while scrolling.
            xml.WriteStartElement("sheetViews", MainNs);
            xml.WriteStartElement("sheetView", MainNs);
            xml.WriteAttributeString("workbookViewId", "0");
            xml.WriteStartElement("pane", MainNs);
            xml.WriteAttributeString("ySplit", "1");
            xml.WriteAttributeString("topLeftCell", "A2");
            xml.WriteAttributeString("activePane", "bottomLeft");
            xml.WriteAttributeString("state", "frozen");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData", MainNs);
        for (var r = 0; r < rows.Count; r++)
        {
            var rowNumber = (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            xml.WriteStartElement("row", MainNs);
            xml.WriteAttributeString("r", rowNumber);

            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (value.Length == 0) continue;

                xml.WriteStartElement("c", MainNs);
                xml.WriteAttributeString("r", ColumnName(c) + rowNumber);

                var isHeader = r == 0;
                if (isHeader) xml.WriteAttributeString("s", "1");

                if (!isHeader && IsPlainNumber(value))
                {
                    xml.WriteElementString("v", MainNs, value);
                }
                else
                {
                    // Inline strings are always literal text: a value starting with '='
                    // is shown as typed, not evaluated as a formula.
                    xml.WriteAttributeString("t", "inlineStr");
                    xml.WriteStartElement("is", MainNs);
                    xml.WriteStartElement("t", MainNs);
                    xml.WriteAttributeString("xml", "space", null, "preserve");
                    xml.WriteString(CleanText(value));
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // sheetData
        xml.WriteEndElement(); // worksheet
        xml.WriteEndDocument();
    }

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    internal static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }
        return name;
    }

    /// <summary>
    /// A decimal number Excel can hold exactly: optional minus sign, no leading zeros
    /// (so IDs like 00123 stay text), no exponent, and at most 15 significant digits.
    /// </summary>
    internal static bool IsPlainNumber(string value)
    {
        if (!PlainNumber().IsMatch(value)) return false;
        var digits = value.Count(char.IsAsciiDigit);
        return digits <= 15;
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();

    /// <summary>Drops characters XML cannot carry and truncates to Excel's cell limit.</summary>
    private static string CleanText(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(ch).Append(value[++i]);
            }
            else if (XmlConvert.IsXmlChar(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.Length <= MaxCellLength ? builder.ToString() : builder.ToString(0, MaxCellLength);
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
        <Default Extension="xml" ContentType="application/xml"/>
        <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
        <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;

    private const string RootRelsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private const string WorkbookRelsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
        <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    // Style 0 is the default; style 1 is the bold header.
    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
        <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
        <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
        <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
        <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
        <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>
        <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}

/// <summary>
/// RFC 4180 CSV parser: quoted fields, doubled quotes, and line breaks inside quotes.
/// If the first line contains tabs but no commas the input is read as tab-separated,
/// which is what SQL Server Management Studio puts on the clipboard.
/// </summary>
public static class CsvParser
{
    public static List<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(text)) return rows;

        if (text[0] == '﻿') text = text[1..];

        var delimiter = DetectDelimiter(text);
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(ch);
                fieldStarted = true;
            }
        }

        // A trailing newline does not start an extra empty row.
        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var firstLine = end < 0 ? text : text[..end];
        return firstLine.Contains('\t') && !firstLine.Contains(',') ? '\t' : ',';
    }
}
