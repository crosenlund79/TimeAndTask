using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace WorkTrace.Infrastructure.Reporting;

/// <summary>Cell look for <see cref="StyledXlsx"/>. Colours are "RRGGBB".</summary>
internal sealed record XStyle(
    bool Bold = false,
    bool Italic = false,
    string? FontColor = null,
    double FontSize = 10,
    string FontName = "Calibri",
    string? Fill = null,
    string Pattern = "solid",
    string? PatternBack = null,
    int NumberFormat = 0,
    string? BorderLeft = null,
    string? BorderRight = null,
    string? BorderTop = null,
    string? BorderBottom = null,
    string BorderColor = "D9D0C0",
    string RightColor = "D9D0C0",
    string? Horizontal = null,
    bool Wrap = false);

/// <summary>One worksheet: cells (row, column 1-based), column widths, frozen panes, filter range, landscape printing.</summary>
internal sealed class XSheet(string name)
{
    public string Name { get; } = name;

    public SortedDictionary<int, SortedDictionary<int, (object? Value, XStyle? Style)>> Rows { get; } = [];

    public Dictionary<int, double> Widths { get; } = [];

    public Dictionary<int, double> Heights { get; } = [];

    /// <summary>First unfrozen cell (e.g. row 5, column 9 freezes A1:H4).</summary>
    public (int Row, int Column)? FreezeAt { get; set; }

    public (int Row1, int Col1, int Row2, int Col2)? AutoFilter { get; set; }

    public bool Landscape { get; set; }

    public void Set(int row, int column, object? value, XStyle? style = null)
    {
        if (!Rows.TryGetValue(row, out var cells))
        {
            Rows[row] = cells = [];
        }

        cells[column] = (value, style);
    }

    public static string Ref(int row, int column) => Column(column) + row.ToString(CultureInfo.InvariantCulture);

    public static string Column(int column)
    {
        var name = "";
        for (var n = column; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }
}

/// <summary>
/// Small .xlsx writer with styles (fonts, fills incl. patterns, borders, number/date formats), column widths, frozen
/// panes and filters. Strings are inline; <see cref="DateOnly"/> becomes a real Excel date. No third-party code.
/// </summary>
internal static class StyledXlsx
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static async Task WriteAsync(string path, IReadOnlyList<XSheet> sheets, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var styles = new StyleTable();
            var sheetXml = sheets.Select(s => SheetXml(s, styles)).ToList(); // registers the styles first
            await Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + string.Concat(sheets.Select((_, i) =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))
                + "</Types>", cancellationToken);
            await Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
                cancellationToken);

            var names = string.Concat(sheets.Select((s, i) => s.AutoFilter is { } f
                ? $"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">{SecurityElement.Escape(Quoted(s.Name))}!${XSheet.Column(f.Col1)}${f.Row1}:${XSheet.Column(f.Col2)}${f.Row2}</definedName>"
                : ""));
            await Add(zip, "xl/workbook.xml",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{Main}\" xmlns:r=\"{Rel}\"><sheets>"
                + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{SecurityElement.Escape(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))
                + "</sheets>" + (names.Length > 0 ? $"<definedNames>{names}</definedNames>" : "") + "</workbook>", cancellationToken);
            await Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(sheets.Select((_, i) =>
                    $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>",
                cancellationToken);
            await Add(zip, "xl/styles.xml", styles.Xml(), cancellationToken);
            for (var i = 0; i < sheets.Count; i++)
            {
                await Add(zip, $"xl/worksheets/sheet{i + 1}.xml", sheetXml[i], cancellationToken);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string Quoted(string sheetName) => $"'{sheetName.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task Add(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        await entry.WriteAsync(new UTF8Encoding(false).GetBytes(content), cancellationToken);
    }

    private static string SheetXml(XSheet sheet, StyleTable styles)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{Main}\" xmlns:r=\"{Rel}\">");
        if (sheet.Landscape)
        {
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>"); // print one page wide (as many pages tall as needed)
        }

        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"");
        if (sheet.FreezeAt is { } f && (f.Row > 1 || f.Column > 1))
        {
            var pane = f.Row > 1 && f.Column > 1 ? "bottomRight" : f.Row > 1 ? "bottomLeft" : "topRight";
            sb.Append(inv, $"><pane{(f.Column > 1 ? $" xSplit=\"{f.Column - 1}\"" : "")}{(f.Row > 1 ? $" ySplit=\"{f.Row - 1}\"" : "")} ")
                .Append(inv, $"topLeftCell=\"{XSheet.Ref(f.Row, f.Column)}\" activePane=\"{pane}\" state=\"frozen\"/>")
                .Append(inv, $"<selection pane=\"{pane}\" activeCell=\"{XSheet.Ref(f.Row, f.Column)}\" sqref=\"{XSheet.Ref(f.Row, f.Column)}\"/></sheetView>");
        }
        else
        {
            sb.Append("/>");
        }

        sb.Append("</sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
        if (sheet.Widths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (column, width) in sheet.Widths.OrderBy(w => w.Key))
            {
                sb.Append(inv, $"<col min=\"{column}\" max=\"{column}\" width=\"{width:0.##}\" customWidth=\"1\"/>");
            }

            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        foreach (var (row, cells) in sheet.Rows)
        {
            sb.Append(inv, $"<row r=\"{row}\"");
            if (sheet.Heights.TryGetValue(row, out var height))
            {
                sb.Append(inv, $" ht=\"{height:0.##}\" customHeight=\"1\"");
            }

            sb.Append('>');
            foreach (var (column, (value, style)) in cells)
            {
                var s = style is null ? 0 : styles.Index(style);
                var r = XSheet.Ref(row, column);
                var sAttr = s == 0 ? "" : $" s=\"{s}\"";
                switch (value)
                {
                    case null:
                    case string { Length: 0 }:
                        sb.Append(inv, $"<c r=\"{r}\"{sAttr}/>");
                        break;
                    case DateOnly date:
                        sb.Append(inv, $"<c r=\"{r}\"{sAttr}><v>{date.DayNumber - new DateOnly(1899, 12, 30).DayNumber}</v></c>");
                        break;
                    case int or long or double or decimal:
                        sb.Append(inv, $"<c r=\"{r}\"{sAttr}><v>{Convert.ToString(value, inv)}</v></c>");
                        break;
                    default:
                        var text = value.ToString() ?? "";
                        if (text.Length > 32000)
                        {
                            text = text[..32000] + "…";
                        }

                        sb.Append(inv, $"<c r=\"{r}\" t=\"inlineStr\"{sAttr}><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
                        break;
                }
            }

            sb.Append("</row>");
        }

        sb.Append("</sheetData>");
        if (sheet.AutoFilter is { } a)
        {
            sb.Append(inv, $"<autoFilter ref=\"{XSheet.Ref(a.Row1, a.Col1)}:{XSheet.Ref(a.Row2, a.Col2)}\"/>");
        }

        sb.Append("<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/>");
        if (sheet.Landscape)
        {
            sb.Append("<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        }

        return sb.Append("</worksheet>").ToString();
    }

    /// <summary>XML-escapes text and drops characters XML 1.0 does not allow.</summary>
    private static string Escape(string text) =>
        SecurityElement.Escape(new string([.. text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ' && c != '￾' && c != '￿')]));

    private sealed class StyleTable
    {
        private readonly List<string> _fonts = ["<font><sz val=\"10\"/><name val=\"Calibri\"/></font>"];
        private readonly List<string> _fills = ["<fill><patternFill patternType=\"none\"/></fill>", "<fill><patternFill patternType=\"gray125\"/></fill>"];
        private readonly List<string> _borders = ["<border><left/><right/><top/><bottom/><diagonal/></border>"];
        private readonly List<string> _xfs = ["<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"];
        private readonly Dictionary<XStyle, int> _index = [];

        public int Index(XStyle style)
        {
            if (_index.TryGetValue(style, out var existing))
            {
                return existing;
            }

            var inv = CultureInfo.InvariantCulture;
            var font = Intern(_fonts, "<font>" + (style.Bold ? "<b/>" : "") + (style.Italic ? "<i/>" : "")
                + string.Create(inv, $"<sz val=\"{style.FontSize:0.#}\"/>")
                + (style.FontColor is { } fc ? $"<color rgb=\"FF{fc}\"/>" : "") + $"<name val=\"{SecurityElement.Escape(style.FontName)}\"/></font>");
            var fill = style.Fill is { } fg
                ? Intern(_fills, $"<fill><patternFill patternType=\"{style.Pattern}\"><fgColor rgb=\"FF{fg}\"/><bgColor rgb=\"FF{style.PatternBack ?? "FFFFFF"}\"/></patternFill></fill>")
                : 0;
            string Side(string tag, string? kind, string color) => kind is null ? $"<{tag}/>" : $"<{tag} style=\"{kind}\"><color rgb=\"FF{color}\"/></{tag}>";
            var border = Intern(_borders, "<border>" + Side("left", style.BorderLeft, style.BorderColor) + Side("right", style.BorderRight, style.RightColor)
                + Side("top", style.BorderTop, style.BorderColor) + Side("bottom", style.BorderBottom, style.BorderColor) + "<diagonal/></border>");
            var alignment = style.Horizontal is null && !style.Wrap
                ? ""
                : "<alignment" + (style.Horizontal is { } h ? $" horizontal=\"{h}\"" : "") + (style.Wrap ? " wrapText=\"1\"" : "") + " vertical=\"center\"/>";
            _xfs.Add($"<xf numFmtId=\"{style.NumberFormat}\" fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\" applyFont=\"1\""
                + (fill > 0 ? " applyFill=\"1\"" : "") + (border > 0 ? " applyBorder=\"1\"" : "") + (style.NumberFormat > 0 ? " applyNumberFormat=\"1\"" : "")
                + (alignment.Length > 0 ? $" applyAlignment=\"1\">{alignment}</xf>" : "/>"));
            return _index[style] = _xfs.Count - 1;
        }

        public string Xml() =>
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"{Main}\">"
            + "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"0.0#\"/><numFmt numFmtId=\"165\" formatCode=\"ddd d mmm yyyy\"/></numFmts>"
            + $"<fonts count=\"{_fonts.Count}\">{string.Concat(_fonts)}</fonts>"
            + $"<fills count=\"{_fills.Count}\">{string.Concat(_fills)}</fills>"
            + $"<borders count=\"{_borders.Count}\">{string.Concat(_borders)}</borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + $"<cellXfs count=\"{_xfs.Count}\">{string.Concat(_xfs)}</cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

        private static int Intern(List<string> list, string xml)
        {
            var i = list.IndexOf(xml);
            if (i >= 0)
            {
                return i;
            }

            list.Add(xml);
            return list.Count - 1;
        }
    }
}
