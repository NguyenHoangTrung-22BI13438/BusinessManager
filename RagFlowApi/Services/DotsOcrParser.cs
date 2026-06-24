using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Logging;

namespace RagFlowApi.Services;

public class DotsOcrParser : IParser
{
    private readonly DotsOcrClient _ocr;
    private readonly ILogger<DotsOcrParser> _log;

    public DotsOcrParser(DotsOcrClient ocr, ILogger<DotsOcrParser> log)
    {
        _ocr = ocr;
        _log = log;
    }

    private static bool IsTextLayerUsable(string extractedText)
    {
        if (string.IsNullOrWhiteSpace(extractedText)) return false;

        var words = extractedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 10) return false; // too sparse

        // Check ratio of non-ASCII / replacement characters
        // Bad scanner OCR on Vietnamese produces lots of ? and garbage
        var garbageChars = extractedText.Count(c => c == '?' || c == '□' || c == '�');
        var garbageRatio = (double)garbageChars / extractedText.Length;
        if (garbageRatio > 0.05) return false; // more than 5% garbage → use OCR

        // Check average word length — real Vietnamese words are 1-6 chars
        // Scanner OCR often produces long garbage strings
        var avgWordLen = words.Average(w => w.Length);
        if (avgWordLen > 15) return false;

        return true;
    }

    public async Task<string> ParseAsync(Stream stream, string fileName, string contentType)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext is ".txt" or ".md")
            return System.Text.Encoding.UTF8.GetString(bytes);

        if (ext == ".docx")
            return ExtractDocx(bytes);

        if (ext == ".xlsx")
            return ExtractXlsx(bytes);

        if (ext == ".pdf")
            return await ExtractPdfSmart(bytes);

        if (contentType.StartsWith("image/"))
        {
            var rawJson = await _ocr.OcrImageAsync(bytes, contentType);
            return FormatOcrOutput(rawJson);
        }

        throw new NotSupportedException($"Unsupported format: {contentType} ({ext})");
    }

    private static string FormatOcrOutput(string ocrJson)
    {
        if (IsSvgContent(ocrJson))
            return ExtractTextFromSvg(ocrJson);

        try
        {
            using var doc = JsonDocument.Parse(ocrJson);
            var sb = new System.Text.StringBuilder();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var category = el.GetProperty("category").GetString() ?? "";
                if (category is "Page-header" or "Page-footer") continue;

                if (el.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }
        catch
        {
            return ocrJson;
        }
    }

    private async Task<string> ExtractPdfSmart(byte[] pdfBytes)
    {
        var pageTexts = new Dictionary<int, string>();
        var emptyPages = new HashSet<int>();
        int totalPages = 0;

        using (var pdf = PdfDocument.Open(pdfBytes))
        {
            int p = 0;
            foreach (var page in pdf.GetPages())
            {
                p++;
                totalPages = p;
                var text = page.Text ?? "";
                var usable = IsTextLayerUsable(text);
                _log.LogDebug("Page {P} text length: {Len}, usable: {U}", p, text.Length, usable);

                if (usable)
                    pageTexts[p] = text;
                else
                    emptyPages.Add(p);
            }
        }

        if (emptyPages.Count > 0)
        {
            int ocrPageNum = 0;
#pragma warning disable CA1416
            await foreach (var bitmap in
    PDFtoImage.Conversion.ToImagesAsync(
        new MemoryStream(pdfBytes),
        options: new PDFtoImage.RenderOptions(Dpi: 300)))
#pragma warning restore CA1416
            {
                ocrPageNum++;
                if (emptyPages.Contains(ocrPageNum))
                {
                    try
                    {
                        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        var preprocessed = ImagePreprocessor.Preprocess(data.ToArray());
                        var rawJson = await _ocr.OcrImageAsync(preprocessed, "image/png");
                        var ocrText = FormatOcrOutput(rawJson);
                        if (!string.IsNullOrWhiteSpace(ocrText))
                            pageTexts[ocrPageNum] = ocrText;
                    }
                    catch { }
                    finally { bitmap.Dispose(); }
                }
                else
                {
                    bitmap.Dispose();
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= totalPages; i++)
        {
            if (pageTexts.TryGetValue(i, out var text))
            {
                sb.AppendLine($"[PAGE {i}]");
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }

    private static string ExtractDocx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";

        var sb = new System.Text.StringBuilder();

        // Walk top-level body children so tables are emitted as whole HTML
        // blocks (matching the vision OCR's table format) instead of being
        // shredded into one-paragraph-per-cell text by Descendants<Paragraph>.
        foreach (var el in body.Elements())
        {
            if (el is DocumentFormat.OpenXml.Wordprocessing.Table table)
            {
                sb.AppendLine();
                sb.AppendLine("<table>");
                foreach (var row in table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                {
                    sb.Append("<tr>");
                    foreach (var cell in row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
                        sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(cell.InnerText.Trim())}</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
                sb.AppendLine();
            }
            else if (el is DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
            {
                var text = para.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
        }
        return sb.ToString();
    }

    private static string ExtractXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var sb = new System.Text.StringBuilder();

        foreach (var sheet in wb.Worksheets)
        {
            sb.AppendLine($"## {sheet.Name}");
            sb.AppendLine();
            foreach (var row in sheet.RowsUsed())
            {
                var cells = row.CellsUsed().Select(c => c.GetFormattedString());
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Shared SVG helpers ────────────────────────────────────────────────────

    internal static bool IsSvgContent(string s) =>
        s.AsSpan().TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);

    internal static string ExtractTextFromSvg(string svg)
    {
        var matches = Regex.Matches(svg,
            @"<text\b[^>]*>(.*?)</text>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var sb = new System.Text.StringBuilder();
        foreach (Match m in matches)
        {
            var inner = Regex.Replace(m.Groups[1].Value, @"<[^>]+>", "");
            var text  = System.Net.WebUtility.HtmlDecode(inner).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }
        return sb.Length > 0 ? sb.ToString() : svg;
    }
}
