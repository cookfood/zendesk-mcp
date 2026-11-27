using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Reads the cell values out of an Excel (.xlsx) workbook and renders each sheet as CSV
/// text, so the model can read a spreadsheet attached to a ticket. Written directly
/// against the Open XML package format, like <see cref="XlsxWriter"/>.
///
/// Formulas are not evaluated: a formula cell gives the value Excel last cached for it.
/// Numbers in a date or time format are written as ISO dates (yyyy-MM-dd, with the time
/// when there is one). Empty rows are skipped and trailing empty cells are dropped.
/// </summary>
public static class XlsxReader
{
    /// <summary>Cap on the XML read out of the package, so a small zip cannot expand without limit.</summary>
    internal const long MaxUncompressedBytes = 200L * 1024 * 1024;

    /// <summary>Cap on the CSV text produced; anything after it is cut with a note.</summary>
    internal const int MaxOutputChars = 20_000_000;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
    };

    /// <summary>True if <paramref name="data"/> is a zip package containing an Excel workbook.</summary>
    public static bool IsXlsx(byte[] data)
    {
        if (data.Length < 4 || data[0] != 'P' || data[1] != 'K' || data[2] != 3 || data[3] != 4)
            return false;

        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            return zip.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns every sheet as a "## Sheet" heading followed by its rows as CSV.
    /// Throws <see cref="InvalidOperationException"/> if the workbook cannot be read.
    /// </summary>
    public static string ToText(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var budget = new ReadBudget(MaxUncompressedBytes);
            return Render(zip, budget);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or FormatException)
        {
            throw new InvalidOperationException($"The Excel workbook could not be read: {ex.Message}", ex);
        }
    }

    private static string Render(ZipArchive zip, ReadBudget budget)
    {
        var workbook = LoadXml(zip, "xl/workbook.xml", budget)
            ?? throw new InvalidDataException("xl/workbook.xml is missing.");
        var rels = LoadRelationships(zip, "xl/_rels/workbook.xml.rels", budget);

        var date1904 = (string?)workbook.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";
        var sharedStrings = LoadSharedStrings(zip, rels, budget);
        var dateStyles = LoadDateStyles(zip, rels, budget);

        var sheets = workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet").ToList() ?? [];
        var output = new StringBuilder();

        for (var i = 0; i < sheets.Count; i++)
        {
            var name = (string?)sheets[i].Attribute("name") ?? $"Sheet{i + 1}";
            var state = (string?)sheets[i].Attribute("state");
            var relId = (string?)sheets[i].Attribute(RelNs + "id");
            var path = relId is not null && rels.TryGetValue(relId, out var rel) ? rel.Path : null;

            var heading = $"## Sheet {i + 1}: {name}";
            if (state is "hidden" or "veryHidden") heading += " (hidden)";

            if (path is null || zip.GetEntry(path) is not { } entry)
            {
                output.Append(heading).Append(" (no cell data: chart sheet or missing part)\n\n");
                continue;
            }

            var csv = new StringBuilder();
            var rowCount = WriteSheetCsv(entry, budget, sharedStrings, dateStyles, date1904, csv);
            output.Append(heading).Append($" ({rowCount} non-empty rows)\n");
            output.Append(csv).Append('\n');

            if (output.Length > MaxOutputChars)
            {
                output.Length = MaxOutputChars;
                output.Append($"\n[Workbook text cut at {MaxOutputChars} characters.]\n");
                break;
            }
        }

        if (sheets.Count == 0) output.Append("The workbook has no sheets.\n");
        return output.ToString();
    }

    private static int WriteSheetCsv(
        ZipArchiveEntry entry,
        ReadBudget budget,
        IReadOnlyList<string> sharedStrings,
        IReadOnlySet<int> dateStyles,
        bool date1904,
        StringBuilder csv)
    {
        using var stream = budget.Open(entry);
        using var reader = XmlReader.Create(stream, SafeXml);

        var rowCount = 0;
        var cells = new List<string>();

        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row" || reader.NamespaceURI != Main.NamespaceName)
            {
                reader.Read();
                continue;
            }

            var row = (XElement)XNode.ReadFrom(reader);
            cells.Clear();

            var next = 0;
            foreach (var c in row.Elements(Main + "c"))
            {
                var column = ColumnIndex((string?)c.Attribute("r")) ?? next;
                while (cells.Count < column) cells.Add(string.Empty);

                var value = CellValue(c, sharedStrings, dateStyles, date1904);
                if (cells.Count == column) cells.Add(value);
                else cells[column] = value;

                next = column + 1;
            }

            var width = cells.Count;
            while (width > 0 && cells[width - 1].Length == 0) width--;
            if (width == 0) continue;

            for (var i = 0; i < width; i++)
            {
                if (i > 0) csv.Append(',');
                AppendCsvField(csv, cells[i]);
            }
            csv.Append('\n');
            rowCount++;

            if (csv.Length > MaxOutputChars) break;
        }

        return rowCount;
    }

    private static string CellValue(XElement c, IReadOnlyList<string> sharedStrings, IReadOnlySet<int> dateStyles, bool date1904)
    {
        var type = (string?)c.Attribute("t") ?? "n";
        var v = (string?)c.Element(Main + "v");

        switch (type)
        {
            case "s":
                return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                    ? sharedStrings[index]
                    : string.Empty;
            case "inlineStr":
                return c.Element(Main + "is") is { } inline ? RichText(inline) : string.Empty;
            case "b":
                return v == "1" ? "TRUE" : v == "0" ? "FALSE" : v ?? string.Empty;
            case "str":
            case "e":
            case "d":
                return v ?? string.Empty;
        }

        if (string.IsNullOrEmpty(v)) return string.Empty;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return v;

        var style = (int?)c.Attribute("s") ?? 0;
        if (dateStyles.Contains(style)) return FormatDate(number, date1904) ?? v;

        // G15 matches the precision Excel shows, so 0.1 + 0.2 reads as 0.3.
        return number.ToString("G15", CultureInfo.InvariantCulture);
    }

    internal static string? FormatDate(double serial, bool date1904)
    {
        if (date1904) serial += 1462;
        if (serial < 0 || serial > 2_958_465) return null;

        DateTime value;
        try
        {
            value = DateTime.FromOADate(serial);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // A serial below 1 is a time of day with no date.
        if (serial < 1) return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"A1" → 0, "AA7" → 26. Null if the reference has no column letters.</summary>
    internal static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;

        var index = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            letters++;
            if (letters > 3) return null;
        }

        return letters == 0 ? null : index - 1;
    }

    private static void AppendCsvField(StringBuilder csv, string value)
    {
        var quote = value.Length > 0 &&
            (value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ');

        if (!quote)
        {
            csv.Append(value);
            return;
        }

        csv.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }

    /// <summary>Text of a shared or inline string: plain t, or the t of each run. Phonetic hints are skipped.</summary>
    private static string RichText(XElement element)
    {
        var direct = element.Elements(Main + "t").Select(t => t.Value);
        var runs = element.Elements(Main + "r").Elements(Main + "t").Select(t => t.Value);
        return string.Concat(direct.Concat(runs));
    }

    private static List<string> LoadSharedStrings(ZipArchive zip, Dictionary<string, Relationship> rels, ReadBudget budget)
    {
        var path = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal))?.Path
            ?? "xl/sharedStrings.xml";

        var strings = new List<string>();
        if (zip.GetEntry(path) is not { } entry) return strings;

        using var stream = budget.Open(entry);
        using var reader = XmlReader.Create(stream, SafeXml);

        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si" && reader.NamespaceURI == Main.NamespaceName)
            {
                strings.Add(RichText((XElement)XNode.ReadFrom(reader)));
            }
            else
            {
                reader.Read();
            }
        }

        return strings;
    }

    /// <summary>Indexes into cellXfs whose number format shows a date or time.</summary>
    private static HashSet<int> LoadDateStyles(ZipArchive zip, Dictionary<string, Relationship> rels, ReadBudget budget)
    {
        var dateStyles = new HashSet<int>();
        var path = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal))?.Path
            ?? "xl/styles.xml";

        var styles = LoadXml(zip, path, budget);
        if (styles?.Root is null) return dateStyles;

        var customDateFormats = styles.Root.Element(Main + "numFmts")?.Elements(Main + "numFmt")
            .Where(f => IsDateFormatCode((string?)f.Attribute("formatCode")))
            .Select(f => (int?)f.Attribute("numFmtId"))
            .OfType<int>()
            .ToHashSet() ?? [];

        var xfs = styles.Root.Element(Main + "cellXfs")?.Elements(Main + "xf").ToList() ?? [];
        for (var i = 0; i < xfs.Count; i++)
        {
            var id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
            if (IsBuiltInDateFormat(id) || customDateFormats.Contains(id)) dateStyles.Add(i);
        }

        return dateStyles;
    }

    private static bool IsBuiltInDateFormat(int id) => id is >= 14 and <= 22 or >= 45 and <= 47;

    /// <summary>
    /// True if a custom number format shows a date or time: after removing quoted text,
    /// escaped characters, and [colour]/[$-locale] sections, it still has d, m, y, h, or s.
    /// </summary>
    internal static bool IsDateFormatCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        var stripped = new StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"')
            {
                var close = code.IndexOf('"', i + 1);
                i = close < 0 ? code.Length : close;
            }
            else if (ch is '\\' or '_' or '*')
            {
                i++;
            }
            else if (ch == '[')
            {
                var close = code.IndexOf(']', i + 1);
                var inner = close < 0 ? string.Empty : code[(i + 1)..close];
                // [h], [mm], and [ss] are elapsed-time formats; keep them, drop colours and locales.
                if (inner.Length > 0 && inner.All(x => x is 'h' or 'H' or 'm' or 'M' or 's' or 'S')) stripped.Append(inner);
                i = close < 0 ? code.Length : close;
            }
            else
            {
                stripped.Append(ch);
            }
        }

        var text = stripped.ToString();
        if (text.Contains("General", StringComparison.OrdinalIgnoreCase)) return false;
        return text.IndexOfAny(['d', 'D', 'm', 'M', 'y', 'Y', 'h', 'H', 's', 'S']) >= 0;
    }

    private sealed record Relationship(string Type, string Path);

    private static Dictionary<string, Relationship> LoadRelationships(ZipArchive zip, string relsPath, ReadBudget budget)
    {
        var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        var rels = LoadXml(zip, relsPath, budget);
        if (rels?.Root is null) return result;

        foreach (var rel in rels.Root.Elements(PackageRels + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            var type = (string?)rel.Attribute("Type") ?? string.Empty;
            if (id is null || target is null || (string?)rel.Attribute("TargetMode") == "External") continue;

            // Targets are relative to xl/ unless they start with a slash.
            var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
            result[id] = new Relationship(type, NormalisePath(path));
        }

        return result;
    }

    private static string NormalisePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static XDocument? LoadXml(ZipArchive zip, string path, ReadBudget budget)
    {
        if (zip.GetEntry(path) is not { } entry) return null;
        using var stream = budget.Open(entry);
        using var reader = XmlReader.Create(stream, SafeXml);
        return XDocument.Load(reader);
    }

    /// <summary>Shared limit on the bytes decompressed across every part read from the package.</summary>
    private sealed class ReadBudget(long limit)
    {
        private long _remaining = limit;

        public Stream Open(ZipArchiveEntry entry) => new BudgetStream(entry.Open(), this);

        public void Spend(int bytes)
        {
            _remaining -= bytes;
            if (_remaining < 0)
                throw new InvalidDataException($"The workbook expands to more than {MaxUncompressedBytes / (1024 * 1024)} MB of XML.");
        }
    }

    private sealed class BudgetStream(Stream inner, ReadBudget budget) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            budget.Spend(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            budget.Spend(read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
