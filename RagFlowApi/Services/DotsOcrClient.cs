using Microsoft.Extensions.Configuration;
using RagFlowApi.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RagFlowApi.Services;

public class DotsOcrClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DotsOcrClient> _log;
    private const string Model = "/workspace/weights/dots.mocr";

    // Stricter retry prompt — forces JSON array output explicitly.
    private const string RetryPrompt =
        "Your previous response was not valid JSON. " +
        "You MUST respond with ONLY a raw JSON array and nothing else — " +
        "no explanation, no markdown fences, no extra text. " +
        "Each element must have \"bbox\", \"category\", and \"text\" fields. " +
        "Example: [{\"bbox\":[0,0,100,50],\"category\":\"Title\",\"text\":\"Hello\"}]";

    public DotsOcrClient(HttpClient http, IConfiguration cfg, ILogger<DotsOcrClient> log)
    {
        _http = http;
        _log  = log;
        _http.BaseAddress = new Uri(cfg["DotsOcr:BaseUrl"]!);
        // 10 minutes per request — vision inference on large/complex pages can be slow.
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Calls the OCR model and returns its raw content string.
    /// If the first response is not valid JSON or SVG, retries once with an explicit
    /// correction prompt. Timeout exceptions return an empty JSON array so the caller
    /// can continue with remaining pages instead of aborting.
    /// </summary>
    public async Task<string> OcrImageAsync(byte[] imageBytes, string mimeType = "image/png")
    {
        var b64Url = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";

        var userMessage = new
        {
            role = "user",
            content = new object[]
            {
                new { type = "image_url", image_url = new { url = b64Url } },
                new
                {
                    type = "text",
                    text = @"Please output the layout information from the document image, including each layout element's bbox, its category, and the corresponding text content within the bbox.

1. Bbox format: [x1, y1, x2, y2]

2. Layout Categories: ['Caption', 'Footnote', 'Formula', 'List-item', 'Page-footer', 'Page-header', 'Picture', 'Section-header', 'Table', 'Text', 'Title'].

3. Text Extraction & Formatting Rules:
    - Picture: Omit the text field.
    - Formula: Format as LaTeX.
    - Table: Format as HTML.
    - All Others: Format as Markdown.
    - Do NOT transcribe handwritten signatures, ink marks, or circular/rectangular stamps.

IMPORTANT: Respond with ONLY a valid JSON array. No markdown code fences, no explanation. Assign category 'Picture' to handwritten signatures, ink marks, and rubber stamps — do not transcribe their text."
                }
            }
        };

        // ── First attempt ─────────────────────────────────────────────────────
        string firstResult;
        try
        {
            firstResult = await CallModelAsync(new[] { userMessage });
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("[OCR] Request timed out on first attempt. Returning empty result.");
            return "[]";
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("[OCR] HTTP error on first attempt: {Msg}. Returning empty result.", ex.Message);
            return "[]";
        }

        if (IsUsableOutput(firstResult))
        {
            if (IsHallucinated(firstResult))
                _log.LogWarning("[OCR] First attempt passed JSON check but appears hallucinated. Retrying.");
            else
                return firstResult;
        }

        _log.LogWarning("[OCR] Model returned unusable output (length={Len}). Retrying with correction prompt.", firstResult.Length);

        // ── Retry: send the bad response back and ask the model to correct it ─
        var assistantEcho = new { role = "assistant", content = firstResult };
        var correctionMsg = new { role = "user",      content = RetryPrompt };

        string retryResult;
        try
        {
            retryResult = await CallModelAsync(new object[] { userMessage, assistantEcho, correctionMsg });
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("[OCR] Retry timed out. Falling back to first result.");
            return firstResult.Length > 0 ? firstResult : "[]";
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("[OCR] HTTP error on retry: {Msg}. Falling back to first result.", ex.Message);
            return firstResult.Length > 0 ? firstResult : "[]";
        }

        if (IsUsableOutput(retryResult))
        {
            if (IsHallucinated(retryResult))
                _log.LogWarning("[OCR] Retry also hallucinated. Returning empty.");
            else
                return retryResult;
        }

        _log.LogWarning("[OCR] Retry also unusable (length={Len}). Returning empty.", retryResult.Length);
        return "[]";
    }

    private static bool IsUsableOutput(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (DotsOcrParser.IsSvgContent(s)) return true;

        var trimmed = s.TrimStart();

        // Strip optional markdown code fence ```json … ```
        if (trimmed.StartsWith("```"))
        {
            var fence = trimmed.IndexOf('\n');
            if (fence >= 0) trimmed = trimmed[(fence + 1)..].TrimStart();
            var end = trimmed.LastIndexOf("```");
            if (end > 0) trimmed = trimmed[..end].TrimEnd();
        }

        if (!trimmed.StartsWith('[') && !trimmed.StartsWith('{')) return false;

        try { JsonDocument.Parse(trimmed); return true; }
        catch { return false; }
    }

    private static bool IsHallucinated(string ocrJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(ocrJson);
            var texts = doc.RootElement.EnumerateArray()
                .Select(el => el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (texts.Count < 5) return false;

            var topCount = texts
                .GroupBy(t => t.Trim())
                .OrderByDescending(g => g.Count())
                .Take(2)
                .Sum(g => g.Count());

            return (double)topCount / texts.Count > 0.6;
        }
        catch { return false; }
    }

    private async Task<string> CallModelAsync(object messages)
    {
        var body = new
        {
            model      = Model,
            messages,
            max_tokens = 32768
        };

        var resp = await _http.PostAsJsonAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

        return json
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    public async Task<List<LayoutElement>> ExtractLayoutAsync(byte[] imageBytes, string mimeType)
    {
        var rawJson = await OcrImageAsync(imageBytes, mimeType);
        var elements = ParseOcrJson(rawJson);

        // Without this, IngestAsync would upload a document with zero chunks and
        // mark the job Done with no error — the image silently vanishes from RAG.
        if (elements.Count == 0)
        {
            _log.LogWarning("[OCR] ExtractLayoutAsync returned no elements for {Mime}. Inserting placeholder.", mimeType);
            elements.Add(new LayoutElement
            {
                Category = LayoutCategory.Text,
                Text = "[Image: no content extracted by OCR]",
                Page = 1,
                Bbox = new BBox(0, 0, 1000, 50)
            });
        }

        return elements;
    }

    /// <summary>
    /// Renders every PDF page to PNG and runs OCR on each.
    /// A failure on any single page is logged and skipped — a placeholder element
    /// is inserted so downstream chunking is aware the page existed.
    /// </summary>
    public async Task<List<LayoutElement>> ExtractLayoutFromPdfAsync(byte[] pdfBytes)
    {
        var result = new List<LayoutElement>();
        int pageNum = 0;

#pragma warning disable CA1416
        await foreach (var bitmap in
    PDFtoImage.Conversion.ToImagesAsync(
        new MemoryStream(pdfBytes),
        options: new PDFtoImage.RenderOptions(Dpi: 300)))
#pragma warning restore CA1416
        {
            pageNum++;
            try
            {
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                var preprocessed = ImagePreprocessor.Preprocess(data.ToArray());
                var pageElements = await ExtractLayoutAsync(preprocessed, "image/png");

                if (pageElements.Count == 0)
                {
                    _log.LogWarning("[OCR] Page {Page} returned no elements.", pageNum);
                    result.Add(new LayoutElement
                    {
                        Category = LayoutCategory.Text,
                        Text     = $"[Page {pageNum}: no content extracted]",
                        Page     = pageNum,
                        Bbox     = new BBox(0, 0, 1000, 50)
                    });
                }
                else
                {
                    foreach (var el in pageElements)
                        result.Add(el with { Page = pageNum });
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[OCR] Page {Page} failed: {Msg}. Inserting placeholder and continuing.", pageNum, ex.Message);
                result.Add(new LayoutElement
                {
                    Category = LayoutCategory.Text,
                    Text     = $"[Page {pageNum}: OCR failed — {ex.Message}]",
                    Page     = pageNum,
                    Bbox     = new BBox(0, 0, 1000, 50)
                });
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        _log.LogInformation("[OCR] Finished PDF: {Pages} pages → {Elements} elements.", pageNum, result.Count);
        return result;
    }

    private static List<LayoutElement> ParseOcrJson(string ocrJson)
    {
        if (DotsOcrParser.IsSvgContent(ocrJson))
        {
            var svgText = DotsOcrParser.ExtractTextFromSvg(ocrJson);
            return string.IsNullOrWhiteSpace(svgText)
                ? []
                : [new() { Category = LayoutCategory.Text, Text = svgText,
                        Page = 1, Bbox = new BBox(0, 0, 1000, 1000) }];
        }

        var src = ocrJson.TrimStart();
        if (src.StartsWith("```"))
        {
            var fence = src.IndexOf('\n');
            if (fence >= 0) src = src[(fence + 1)..].TrimStart();
            var end = src.LastIndexOf("```");
            if (end > 0) src = src[..end].TrimEnd();
        }

        try
        {
            using var doc = JsonDocument.Parse(src);
            return CleanElements(ParseElements(doc.RootElement));
        }
        catch (JsonException)
        {
            // JSON was likely truncated by max_tokens — salvage whatever complete
            // elements exist before the cut-off point.
            var lastComplete = src.LastIndexOf("},");
            if (lastComplete > 0)
            {
                var salvaged = src[..lastComplete] + "}]";
                try
                {
                    using var doc = JsonDocument.Parse(salvaged);
                    var elements = CleanElements(ParseElements(doc.RootElement));
                    if (elements.Count > 0)
                        return elements;
                }
                catch { }
            }
            return [];
        }
    }

    private static List<LayoutElement> ParseElements(JsonElement root)
    {
        var result = new List<LayoutElement>();

        foreach (var el in root.EnumerateArray())
        {
            var categoryStr = el.GetProperty("category").GetString() ?? "Text";
            var text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            var category = categoryStr switch
            {
                "Title" => LayoutCategory.Title,
                "Section-header" => LayoutCategory.SectionHeader,
                "Table" => LayoutCategory.Table,
                "List-item" => LayoutCategory.ListItem,
                "Caption" => LayoutCategory.Caption,
                "Footnote" => LayoutCategory.Footnote,
                "Page-header" => LayoutCategory.PageHeader,
                "Page-footer" => LayoutCategory.PageFooter,
                "Formula" => LayoutCategory.Formula,
                "Picture" => LayoutCategory.Picture,
                _ => LayoutCategory.Text
            };

            BBox bbox = new(0, 0, 1000, 50);
            if (el.TryGetProperty("bbox", out var b) && b.GetArrayLength() == 4)
                bbox = new BBox(b[0].GetInt32(), b[1].GetInt32(),
                                b[2].GetInt32(), b[3].GetInt32());

            result.Add(new LayoutElement
            {
                Category = category,
                Text = text,
                Page = 1,
                Bbox = bbox
            });
        }

        return result;
    }

    // ── Post-OCR hallucination cleaning ──────────────────────────────────────

    private static readonly HashSet<string> _englishHallucinations =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","this","that","with","from","have","for","are","was",
        "will","been","they","them","their","what","when","where","which",
        "there","here","about","would","could","should","these","those",
        "test","message","purpose","testing","content","section","ensure",
        "delivered","selectively","obtained","scores","grades","school",
        "catastrophes","literature","boredom","disappear","chromosomes",
        "separate","placeholder","figsize","trumbledore","always","never",
        // Code / programming tokens the model hallucinates from stamp regions
        "urlpatterns","unused","ost","trigger","signatures","extent",
        "benchesfafb","yo","signatures"
    };

    // Returns true if more than 15% of characters are CJK/Arabic — never present
    // in Vietnamese corporate documents, so these are always stamp hallucinations.
    private static bool ContainsForeignScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int foreign = 0;
        foreach (var c in text)
        {
            if (c >= '一' && c <= '鿿') { foreign++; continue; } // CJK
            if (c >= '㐀' && c <= '䶿') { foreign++; continue; } // CJK Ext-A
            if (c >= '぀' && c <= 'ヿ') { foreign++; continue; } // Hiragana/Katakana
            if (c >= '؀' && c <= 'ۿ') { foreign++; continue; } // Arabic
            if ((c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿')) { foreign++; continue; }
            if (c >= '가' && c <= '힯') { foreign++; continue; } // Korean
        }
        return (double)foreign / text.Length > 0.15;
    }

    private static bool IsBadTableCell(string cellText)
    {
        if (string.IsNullOrWhiteSpace(cellText)) return false;
        if (ContainsForeignScript(cellText)) return true;

        var words = cellText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 1)
            return _englishHallucinations.Contains(words[0].Trim('.', ',', ':', ';'));

        int badCount = words.Count(w =>
            _englishHallucinations.Contains(w.Trim('.', ',', ':', ';', '?', '!')));
        return (double)badCount / words.Length > 0.55;
    }

    private static string SanitizeTableHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        html = Regex.Replace(
            html,
            @"(<t[dh][^>]*>)([\s\S]*?)(</t[dh]>)",
            m =>
            {
                var open    = m.Groups[1].Value;
                var inner   = m.Groups[2].Value;
                var close   = m.Groups[3].Value;
                var plainText = Regex.Replace(inner, @"<[^>]+>", " ").Trim();
                return IsBadTableCell(plainText) ? $"{open}{close}" : m.Value;
            },
            RegexOptions.IgnoreCase);

        return html;
    }

    private static List<LayoutElement> CleanElements(List<LayoutElement> elements)
    {
        var result = new List<LayoutElement>();
        foreach (var el in elements)
        {
            if (el.Category == LayoutCategory.Picture)
            {
                result.Add(el);
                continue;
            }

            if (el.Category == LayoutCategory.Table)
            {
                var cleanedHtml = SanitizeTableHtml(el.Text ?? "");
                var plainAfter = Regex.Replace(cleanedHtml, @"<[^>]+>", "").Trim();
                if (!string.IsNullOrWhiteSpace(plainAfter))
                    result.Add(el with { Text = cleanedHtml });
                continue;
            }

            var text = el.Text ?? "";
            if (ContainsForeignScript(text)) continue;
            if (IsPlaceholderText(text)) continue;

            var cleaned = StripInjectedEnglish(text);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Trim().Length < 5) continue;

            result.Add(el with { Text = cleaned });
        }
        return result;
    }

    private static bool IsPlaceholderText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4) return false;

        int englishCount = words.Count(w =>
            _englishHallucinations.Contains(w.Trim('.', ',', ':', ';', '?', '!')));

        return (double)englishCount / words.Length > 0.55;
    }

    private static string StripInjectedEnglish(string text)
    {
        var words = text.Split(' ');
        var kept = new List<string>();

        foreach (var word in words)
        {
            var clean = word.Trim('.', ',', ':', ';', '?', '!');
            bool hasVietnamese = clean.Any(c => c > 127);
            bool isNumber = clean.All(c => char.IsDigit(c) || c == '.' || c == ',');
            bool isShort = clean.Length <= 2;
            bool isEnglishHall = _englishHallucinations.Contains(clean);

            if (hasVietnamese || isNumber || isShort || !isEnglishHall)
                kept.Add(word);
        }

        return string.Join(" ", kept).Trim();
    }
}
