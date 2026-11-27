using System.IO.Compression;
using System.Text;
using ZendeskMcp.Server.Zendesk;

namespace ZendeskMcp.Tests;

public class XlsxReaderTests
{
    [Fact]
    public void ToText_ReadsSharedStringsDatesBooleansAndGaps()
    {
        var workbook = BuildWorkbook(
            sheets: [("Refunds", """
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="D1" t="s"><v>2</v></c></row>
                <row r="2"><c r="A2"><v>1001</v></c><c r="B2" s="1"><v>45658</v></c><c r="D2"><v>0.30000000000000004</v></c></row>
                <row r="3"><c r="A3"><f>A2+1</f><v>1002</v></c><c r="B3" s="2"><v>45658.5</v></c><c r="C3" t="b"><v>1</v></c></row>
                <row r="4" spans="1:4"><c r="A4" s="0"/></row>
                <row r="9"><c r="A9" t="inlineStr"><is><t>note, with comma</t></is></c></row>
                """)],
            sharedStrings: ["order", "<r><t>refund</t></r><r><t xml:space=\"preserve\"> date</t></r><rPh><t>ignored</t></rPh>", "amount"],
            numFmts: """<numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy\ hh:mm"/></numFmts>""",
            cellXfNumFmtIds: [0, 14, 164]);

        var text = XlsxReader.ToText(workbook);

        Assert.Equal(
            "## Sheet 1: Refunds (4 non-empty rows)\n" +
            "order,refund date,,amount\n" +
            "1001,2025-01-01,,0.3\n" +
            "1002,2025-01-01 12:00:00,TRUE\n" +
            "\"note, with comma\"\n\n",
            text);
    }

    [Fact]
    public void ToText_ListsEverySheetAndMarksHiddenOnes()
    {
        var workbook = BuildWorkbook(
            sheets: [("Visible", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>"""),
                     ("Secret", """<row r="1"><c r="A1" t="inlineStr"><is><t>b</t></is></c></row>""")],
            hiddenSheet: 1);

        var text = XlsxReader.ToText(workbook);

        Assert.Contains("## Sheet 1: Visible (1 non-empty rows)\na\n", text);
        Assert.Contains("## Sheet 2: Secret (hidden) (1 non-empty rows)\nb\n", text);
    }

    [Fact]
    public void IsXlsx_IsFalseForOtherZips()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "word/document.xml", "<w/>");
        }

        Assert.False(XlsxReader.IsXlsx(stream.ToArray()));
        Assert.True(XlsxReader.IsXlsx(XlsxWriter.FromCsv("a\n")));
    }

    [Fact]
    public void ToText_RejectsDtd()
    {
        var workbook = BuildWorkbook(
            sheets: [("S", """<row r="1"><c r="A1"><v>1</v></c></row>""")],
            workbookPrefix: """<!DOCTYPE workbook [<!ENTITY x "boom">]>""");

        Assert.Throws<InvalidOperationException>(() => XlsxReader.ToText(workbook));
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("XFD1", 16383)]
    public void ColumnIndex_ParsesCellReferences(string reference, int expected) =>
        Assert.Equal(expected, XlsxReader.ColumnIndex(reference));

    [Theory]
    [InlineData("dd/mm/yyyy", true)]
    [InlineData("[$-809]dd mmmm yyyy", true)]
    [InlineData("hh:mm:ss", true)]
    [InlineData("[h]:mm", true)]
    [InlineData("#,##0.00", false)]
    [InlineData("[Red]#,##0", false)]
    [InlineData("\"days\" 0", false)]
    [InlineData("£#,##0.00", false)]
    [InlineData("General", false)]
    public void IsDateFormatCode_RecognisesDateFormats(string code, bool expected) =>
        Assert.Equal(expected, XlsxReader.IsDateFormatCode(code));

    [Fact]
    public void FormatDate_HandlesThe1904DateSystem() =>
        Assert.Equal("2025-01-01", XlsxReader.FormatDate(45658 - 1462, date1904: true));

    private static byte[] BuildWorkbook(
        (string Name, string Rows)[] sheets,
        string[]? sharedStrings = null,
        string numFmts = "",
        int[]? cellXfNumFmtIds = null,
        int? hiddenSheet = null,
        string workbookPrefix = "")
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sheetList = new StringBuilder();
            var rels = new StringBuilder();
            for (var i = 0; i < sheets.Length; i++)
            {
                var state = hiddenSheet == i ? " state=\"hidden\"" : string.Empty;
                sheetList.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\"{state} r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml",
                    $"<worksheet xmlns=\"{main}\"><sheetData>{sheets[i].Rows}</sheetData></worksheet>");
            }

            rels.Append($"<Relationship Id=\"rIdS\" Type=\"{rel}/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            rels.Append($"<Relationship Id=\"rIdT\" Type=\"{rel}/styles\" Target=\"/xl/styles.xml\"/>");

            Write(zip, "xl/workbook.xml",
                $"{workbookPrefix}<workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets>{sheetList}</sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels",
                $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");

            var si = string.Concat((sharedStrings ?? []).Select(s => s.StartsWith('<') ? $"<si>{s}</si>" : $"<si><t>{s}</t></si>"));
            Write(zip, "xl/sharedStrings.xml", $"<sst xmlns=\"{main}\">{si}</sst>");

            var xfs = string.Concat((cellXfNumFmtIds ?? [0]).Select(id => $"<xf numFmtId=\"{id}\"/>"));
            Write(zip, "xl/styles.xml", $"<styleSheet xmlns=\"{main}\">{numFmts}<cellXfs>{xfs}</cellXfs></styleSheet>");
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
