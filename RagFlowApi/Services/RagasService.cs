using System.Text;
using System.Text.Json;

namespace RagFlowApi.Services;

/// <summary>
/// Implements two RAGAS-equivalent metrics by prompting Gemini directly:
///   - Faithfulness:      are all claims in the answer grounded in the retrieved chunks?
///   - Answer Relevancy: does the answer actually address the question?
///
/// No Python or external library required — same pattern as existing Gemini calls.
/// </summary>
public class RagasService
{
    private readonly IHttpClientFactory _factory;
    private readonly string _geminiApiKey;
    private readonly ILogger<RagasService> _log;

    // Gemini endpoint — flash is fast and cheap enough for eval
    private const string GeminiUrl =
    "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

    public RagasService(
        IHttpClientFactory factory,
        IConfiguration config,
        ILogger<RagasService> log)
    {
        _factory = factory;
        _log = log;
        _geminiApiKey = config["Gemini:ApiKey"]
            ?? throw new InvalidOperationException(
                "Missing config key: Gemini:ApiKey");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures whether every factual claim in <paramref name="answer"/>
    /// is supported by at least one of the retrieved <paramref name="contexts"/>.
    /// Score 0–1; higher is better (no hallucination = 1.0).
    /// </summary>
    public Task<RagasScore> ScoreFaithfulnessAsync(
        string answer, IEnumerable<string> contexts)
    {
        var contextBlock = BuildContextBlock(contexts);

        var prompt = $$"""
            You are a strict factual auditor.

            TASK: Assess whether every claim made in the ANSWER is supported by the CONTEXT passages below.
            Do not use any external knowledge — only judge based on the provided context.

            CONTEXT:
            {{contextBlock}}

            ANSWER:
            {{answer}}

            INSTRUCTIONS:
            1. List each distinct factual claim in the answer.
            2. For each claim, mark it as SUPPORTED or NOT SUPPORTED based solely on the context.
            3. Faithfulness score = (number of supported claims) / (total claims).
               If the answer contains no factual claims, return score 1.0.

            Return ONLY valid JSON in this exact format (no markdown, no explanation outside JSON):
            {"score": <float 0.0 to 1.0>,
              "supported_claims": <int>,
              "total_claims": <int>,
              "reason": "<one sentence summary of findings>"
            }
            """;

        return CallGeminiAsync(prompt);
    }

    /// <summary>
    /// Measures how directly and completely the answer addresses the question.
    /// Score 0–1; higher is better.
    /// </summary>
    public Task<RagasScore> ScoreAnswerRelevancyAsync(
        string question, string answer)
    {
        var prompt = $$"""
            You are an answer quality evaluator.

            TASK: Rate how directly and completely the ANSWER addresses the QUESTION.

            QUESTION:
            {{question}}

            ANSWER:
            {{answer}}

            SCORING GUIDE:
            1.0 — The answer directly and completely addresses the question.
            0.7 — The answer is mostly relevant but misses some aspects.
            0.4 — The answer is partially relevant or too vague.
            0.1 — The answer barely relates to the question.
            0.0 — The answer does not address the question at all.

            Return ONLY valid JSON in this exact format (no markdown, no explanation outside JSON):
            {"score": <float 0.0 to 1.0>,
              "reason": "<one sentence summary of findings>"
            }
            """;

        return CallGeminiAsync(prompt);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildContextBlock(IEnumerable<string> contexts)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var ctx in contexts)
        {
            sb.AppendLine($"[{i}] {ctx.Trim()}");
            sb.AppendLine();
            i++;
        }
        return sb.Length > 0 ? sb.ToString() : "(no context retrieved)";
    }

    private async Task<RagasScore> CallGeminiAsync(string prompt)
    {
        var http = _factory.CreateClient();

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,   // deterministic for eval
                maxOutputTokens = 2048
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{GeminiUrl}?key={_geminiApiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Gemini returned {Code}: {Body}",
                    (int)response.StatusCode, body);
                return RagasScore.Error($"Gemini HTTP {(int)response.StatusCode}");
            }

            // Extract the text content from Gemini's response envelope
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            return ParseScoreJson(text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RagasService Gemini call failed");
            return RagasScore.Error(ex.Message);
        }
    }

    private static RagasScore ParseScoreJson(string raw)
    {
        // Strip markdown code fences if Gemini wraps the JSON
        var clean = raw.Trim();
        if (clean.StartsWith("```"))
        {
            var start = clean.IndexOf('{');
            var end   = clean.LastIndexOf('}');
            if (start >= 0 && end > start)
                clean = clean[start..(end + 1)];
        }

        try
        {
            using var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;

            var score = root.TryGetProperty("score", out var s)
                ? s.GetDouble() : 0.0;
            var reason = root.TryGetProperty("reason", out var r)
                ? r.GetString() ?? "" : "";

            return new RagasScore(Math.Clamp(score, 0.0, 1.0), reason);
        }
        catch
        {
            // Gemini returned something we can't parse — treat as error
            return RagasScore.Error($"Could not parse Gemini response: {raw[..Math.Min(200, raw.Length)]}");
        }
    }
}

/// <summary>
/// Result of a single RAGAS metric evaluation.
/// </summary>
public record RagasScore(double Score, string Reason)
{
    public bool IsError { get; init; } = false;

    public static RagasScore Error(string reason) =>
        new RagasScore(-1.0, reason) { IsError = true };

    /// <summary>Formatted for display: "0.87" or "ERR"</summary>
    public string Display => IsError ? "ERR" : Score.ToString("F2");

    /// <summary>CSS class for colour-coding in the table.</summary>
    public string CssClass => IsError ? "score--error"
        : Score >= 0.7 ? "score--high"
        : Score >= 0.4 ? "score--mid"
        : "score--low";
}
