using System.Net.Http.Json;
using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Calls a locally-running PaddleOCR REST server (see paddle_ocr_server.py).
/// Used as a fallback when DotsOCR returns zero elements — PaddleOCR is lighter
/// and more reliable on clean scanned images but produces only raw text blocks
/// without semantic layout categories.
/// </summary>
public class PaddleOcrClient
{
    private readonly HttpClient _http;
    private readonly ILogger<PaddleOcrClient> _log;
    private readonly bool _enabled;

    public PaddleOcrClient(HttpClient http, IConfiguration cfg, ILogger<PaddleOcrClient> log)
    {
        _http = http;
        _log  = log;

        var baseUrl = cfg["PaddleOcr:BaseUrl"];
        _enabled = !string.IsNullOrWhiteSpace(baseUrl);

        if (_enabled)
        {
            _http.BaseAddress = new Uri(baseUrl!);
            _http.Timeout     = TimeSpan.FromSeconds(60);
        }
    }

    public bool IsEnabled => _enabled;

    /// <summary>
    /// Sends an image to the PaddleOCR server and returns recognized text as
    /// plain Text layout elements. Returns an empty list on any failure.
    /// </summary>
    public async Task<List<LayoutElement>> ExtractTextAsync(byte[] imageBytes, string mimeType)
    {
        if (!_enabled) return [];

        var body = new
        {
            image     = Convert.ToBase64String(imageBytes),
            mime_type = mimeType
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("/ocr", body);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _log.LogWarning("[PaddleOCR] Request failed: {Msg}", ex.Message);
            return [];
        }

        JsonElement json;
        try
        {
            json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch (Exception ex)
        {
            _log.LogWarning("[PaddleOCR] Could not parse response: {Msg}", ex.Message);
            return [];
        }

        return ParseResults(json);
    }

    private static List<LayoutElement> ParseResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)) return [];

        var elements = new List<LayoutElement>();
        foreach (var item in results.EnumerateArray())
        {
            var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            // paddle_ocr_server.py returns [x1, y1, x2, y2] (top-left / bottom-right).
            BBox bbox = new(0, 0, 1000, 50);
            if (item.TryGetProperty("bbox", out var b) && b.GetArrayLength() == 4)
                bbox = new BBox(b[0].GetInt32(), b[1].GetInt32(),
                                b[2].GetInt32(), b[3].GetInt32());

            elements.Add(new LayoutElement
            {
                Category = LayoutCategory.Text,
                Text     = text,
                Page     = 1,
                Bbox     = bbox
            });
        }
        return elements;
    }
}
