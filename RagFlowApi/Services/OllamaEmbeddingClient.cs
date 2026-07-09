using System.Text;
using System.Text.Json;

namespace RagFlowApi.Services;

/// <summary>
/// Calls LMStudio's OpenAI-compatible /v1/embeddings endpoint to produce
/// dense vector embeddings (BGE-M3).
/// Base URL and model name are read from the "Embedding" config section.
/// </summary>
public class OllamaEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<OllamaEmbeddingClient> _log;

    public OllamaEmbeddingClient(
        HttpClient http, IConfiguration config, ILogger<OllamaEmbeddingClient> log)
    {
        _http  = http;
        _log   = log;
        _model = config["Embedding:Model"] ?? "text-embedding-bge-m3";
    }

    /// <summary>
    /// Returns the embedding vector for <paramref name="text"/>, or an empty
    /// array if the embedding server is unreachable.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text)
    {
        // LMStudio uses the OpenAI-compatible format:
        //   POST /v1/embeddings  { "model": "...", "input": "..." }
        //   → { "data": [ { "embedding": [...] } ] }
        var payload = JsonSerializer.Serialize(new { model = _model, input = text });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            var res  = await _http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("Embedding server {Code}: {Body}", (int)res.StatusCode, body);
                return [];
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding")
                .EnumerateArray()
                .Select(e => e.GetSingle())
                .ToArray();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Embedding call failed");
            return [];
        }
    }
}
