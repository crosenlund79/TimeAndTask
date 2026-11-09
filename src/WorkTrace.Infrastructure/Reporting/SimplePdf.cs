using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace WorkTrace.Infrastructure.Reporting;

internal enum PdfFont
{
    Regular,
    Bold,
    Italic,
    Heading,
}

/// <summary>
/// Minimal PDF writer for printable reports: pages with vector rectangles, lines and text in the standard fonts
/// (Helvetica, Times for headings; WinAnsi encoding, so Danish letters work). Coordinates are points from the top-left.
/// No third-party code; nothing leaves the machine.
/// </summary>
internal sealed class SimplePdf(double width, double height)
{
    // Helvetica advance widths (1/1000 em) for ' '..'~'.
    private static readonly short[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556,
        556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556,
        556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = new();

    public double Width { get; } = width;

    public double Height { get; } = height;

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
    }

    public void FillRect(double x, double y, double w, double h, string rgb) =>
        Op($"{Color(rgb)} rg {N(x)} {N(Height - y - h)} {N(w)} {N(h)} re f");

    public void StrokeRect(double x, double y, double w, double h, string rgb, double lineWidth = 0.5, string? dash = null) =>
        Op($"q {Color(rgb)} RG {N(lineWidth)} w {(dash is null ? "" : $"[{dash}] 0 d ")}{N(x)} {N(Height - y - h)} {N(w)} {N(h)} re S Q");

    public void Line(double x1, double y1, double x2, double y2, string rgb, double lineWidth = 0.5, string? dash = null) =>
        Op($"q {Color(rgb)} RG {N(lineWidth)} w {(dash is null ? "" : $"[{dash}] 0 d ")}{N(x1)} {N(Height - y1)} m {N(x2)} {N(Height - y2)} l S Q");

    /// <summary>A filled polygon (e.g. a deadline marker).</summary>
    public void FillPolygon(string rgb, params (double X, double Y)[] points)
    {
        var sb = new StringBuilder($"{Color(rgb)} rg ");
        for (var i = 0; i < points.Length; i++)
        {
            sb.Append(N(points[i].X)).Append(' ').Append(N(Height - points[i].Y)).Append(i == 0 ? " m " : " l ");
        }

        Op(sb.Append("h f").ToString());
    }

    /// <summary>Text with its baseline at <paramref name="y"/>; trimmed with "…" to <paramref name="maxWidth"/>.</summary>
    public void Text(double x, double y, string text, double size, string rgb = "2D2922", PdfFont font = PdfFont.Regular, double? maxWidth = null, bool alignRight = false)
    {
        if (maxWidth is { } max)
        {
            text = Fit(text, size, font, max);
        }

        if (text.Length == 0)
        {
            return;
        }

        if (alignRight)
        {
            x -= Measure(text, size, font);
        }

        Op($"BT /F{(int)font + 1} {N(size)} Tf {Color(rgb)} rg {N(x)} {N(Height - y)} Td ({Encode(text)}) Tj ET");
    }

    public static double Measure(string text, double size, PdfFont font)
    {
        var units = 0.0;
        foreach (var c in text)
        {
            units += c is >= ' ' and <= '~' ? HelveticaWidths[c - ' '] : 600;
        }

        return units * size / 1000 * (font is PdfFont.Bold or PdfFont.Heading ? 1.08 : 1.0);
    }

    public static string Fit(string text, double size, PdfFont font, double maxWidth)
    {
        if (Measure(text, size, font) <= maxWidth)
        {
            return text;
        }

        var end = text.Length;
        while (end > 0 && Measure(text[..end] + "…", size, font) > maxWidth)
        {
            end--;
        }

        return end == 0 ? "" : text[..end].TrimEnd() + "…";
    }

    public void Save(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => output.Write(Encoding.Latin1.GetBytes(s));
        void Begin()
        {
            offsets.Add(output.Position);
            Write(string.Create(inv, $"{offsets.Count} 0 obj\n"));
        }

        Write("%PDF-1.4\n%âãÏÓ\n");
        var fontNames = new[] { "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Times-Bold" };
        var firstPage = 3 + fontNames.Length;

        Begin();
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Begin();
        Write("<< /Type /Pages /Kids [" + string.Join(' ', _pages.Select((_, i) => string.Create(inv, $"{firstPage + (i * 2)} 0 R"))) + string.Create(inv, $"] /Count {_pages.Count} >>\nendobj\n"));
        foreach (var name in fontNames)
        {
            Begin();
            Write($"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /Encoding /WinAnsiEncoding >>\nendobj\n");
        }

        var fonts = string.Join(' ', fontNames.Select((_, i) => string.Create(inv, $"/F{i + 1} {3 + i} 0 R")));
        for (var i = 0; i < _pages.Count; i++)
        {
            Begin();
            Write(string.Create(inv, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(Width)} {N(Height)}] /Resources << /Font << {fonts} >> >> /Contents {firstPage + (i * 2) + 1} 0 R >>\nendobj\n"));
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(Encoding.Latin1.GetBytes(_pages[i].ToString()));
            }

            Begin();
            Write(string.Create(inv, $"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n"));
            compressed.Position = 0;
            compressed.CopyTo(output);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var sb = new StringBuilder(string.Create(inv, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            sb.Append(offset.ToString("D10", inv)).Append(" 00000 n \n");
        }

        sb.Append(inv, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info << /Producer (WorkTrace) >> >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, output.ToArray());
        File.Move(temp, path, overwrite: true);
    }

    private void Op(string op)
    {
        if (_pages.Count == 0)
        {
            NewPage();
        }

        _page.Append(op).Append('\n');
    }

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Color(string rgb)
    {
        var v = Convert.ToInt32(rgb, 16);
        return $"{N(((v >> 16) & 0xFF) / 255.0)} {N(((v >> 8) & 0xFF) / 255.0)} {N((v & 0xFF) / 255.0)}";
    }

    /// <summary>WinAnsi bytes as a PDF string body (Latin-1 as is, a few typographic marks mapped, the rest "?").</summary>
    private static string Encode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var b = c switch
            {
                '–' => '\u0096',
                '—' => '\u0097',
                '…' => '\u0085',
                '‘' => '\u0091',
                '’' => '\u0092',
                '“' => '\u0093',
                '”' => '\u0094',
                '•' => '\u0095',
                '€' => '\u0080',
                '‹' => '\u008B',
                '›' => '\u009B',
                _ when c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' => c,
                _ => '?',
            };
            if (b is '(' or ')' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(b);
        }

        return sb.ToString();
    }
}
