using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Single point of contact for all HTTP calls to the RAGFlow REST API.
/// Handles authentication, serialisation, and response reading.
/// Covers datasets, documents, chunks, chat sessions, completions, and image retrieval.
/// </summary>
public class RagFlowService
{
    private readonly HttpClient _http;
    private readonly ILogger<RagFlowService> _log;
    private readonly IHttpClientFactory _factory;
    private readonly IMemoryCache _cache;
    private readonly ConversationStore _store;
    private readonly HybridRetriever _retriever;
    private readonly string _apiKey;
    private readonly string _embeddingModel;
    private readonly string _geminiApiKey;
    private readonly double _bm25Weight;

    private const string GeminiAnswerUrl =
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public RagFlowService(HttpClient http, ILogger<RagFlowService> log, IConfiguration config,
        IHttpClientFactory factory, IMemoryCache cache, ConversationStore store,
        HybridRetriever retriever)
    {
        _http = http;
        _log = log;
        _factory = factory;
        _cache = cache;
        _store = store;
        _retriever = retriever;
        _apiKey = config["RagFlow:ApiKey"]!;
        _embeddingModel = config["RagFlow:EmbeddingModel"] ?? "bge-m3@Ollama";
        _geminiApiKey = config["Gemini:ApiKey"] ?? "";
        _bm25Weight = double.TryParse(config["Retrieval:Bm25Weight"], out var w) ? w : 0.3;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        return req;
    }

    // ── 2. Upload document ────────────────────────────────────────────────────
    public async Task<string> UploadDocumentAsync(string datasetId, IFormFile file)
    {
        var req = NewRequest(HttpMethod.Post, $"/api/v1/datasets/{datasetId}/documents");
        using var stream = file.OpenReadStream();
        var multipart = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
        multipart.Add(fileContent, "file", file.FileName);
        req.Content = multipart;
        var res = await _http.SendAsync(req);
        return await ReadBodyAsync(res);
    }

    // ── 3. List sessions (typed) ──────────────────────────────────────────────
    public async Task<List<SessionItem>> ListSessionsAsync(string assistantId)
    {
        var req = NewRequest(HttpMethod.Get, $"/api/v1/chats/{assistantId}/sessions");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = new List<SessionItem>();
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                list.Add(new SessionItem(
                    item.GetProperty("id").GetString() ?? "",
                    item.GetProperty("name").GetString() ?? "Unnamed"
                ));
            }
            return list;
        }
        catch { return []; }
    }

    // ── 4. Create chat session ────────────────────────────────────────────────
    public async Task<string?> CreateSessionAsync(string assistantId, string name)
    {
        var req = NewRequest(HttpMethod.Post, $"/api/v1/chats/{assistantId}/sessions");
        req.Content = ToJson(new { name });
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("data")
                .GetProperty("id")
                .GetString();
        }
        catch { return null; }
    }

    // ── 5. Delete a session ───────────────────────────────────────────────────
    public async Task DeleteSessionAsync(string assistantId, string sessionId)
    {
        var req = NewRequest(HttpMethod.Delete, $"/api/v1/chats/{assistantId}/sessions");
        req.Content = ToJson(new { ids = new[] { sessionId } });
        await _http.SendAsync(req);
    }

    // ── 6. Get message history ────────────────────────────────────────────────
    public async Task<List<ChatMessage>> GetMessagesAsync(string assistantId, string sessionId)
    {
        var cacheKey = $"messages:{sessionId}";
        if (_cache.TryGetValue(cacheKey, out List<ChatMessage>? cached) && cached?.Count > 0)
            return cached;

        // File store survives restarts; check it before hitting RAGFlow
        var stored = await _store.LoadAsync(sessionId);
        if (stored?.Count > 0)
        {
            _cache.Set(cacheKey, stored, TimeSpan.FromHours(24));
            return stored;
        }

        return await GetMessagesFromRagFlowAsync(assistantId, sessionId);
    }

    private async Task<List<ChatMessage>> GetMessagesFromRagFlowAsync(string assistantId, string sessionId)
    {
        var req = NewRequest(HttpMethod.Get,
            $"/api/v1/chats/{assistantId}/sessions?id={sessionId}");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var messages = new List<ChatMessage>();
            var msgs = doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .First()
                .GetProperty("messages");

            foreach (var msg in msgs.EnumerateArray())
            {
                var role    = msg.GetProperty("role").GetString() ?? "";
                var content = msg.GetProperty("content").GetString() ?? "";
                var msgId   = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                              ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(msgId))
                    msgId = StableMessageId(role, content);
                messages.Add(new ChatMessage(role, content, msgId) { Chunks = ParseReferences(msg) });
            }
            return messages;
        }
        catch { return []; }
    }

    private static List<RagChunk> ParseReferences(JsonElement msg)
    {
        if (!msg.TryGetProperty("reference", out var refEl)) return [];
        return ParseChunkArray(refEl);
    }

    public static List<RagChunk> ParseCompletionChunks(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("reference", out var refEl)) return [];
            return ParseChunkArray(refEl);
        }
        catch { return []; }
    }

    // RAGFlow returns "reference" as either a bare array of chunks
    // or an object with a "chunks" key — handle both shapes.
    private static List<RagChunk> ParseChunkArray(JsonElement refEl)
    {
        JsonElement chunks;
        if (refEl.ValueKind == JsonValueKind.Array)
            chunks = refEl;
        else if (refEl.ValueKind == JsonValueKind.Object &&
                 refEl.TryGetProperty("chunks", out var c))
            chunks = c;
        else return [];

        static string Str(JsonElement el, params string[] names)
        {
            foreach (var n in names)
                if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            return "";
        }

        var result = new List<RagChunk>();
        foreach (var chunk in chunks.EnumerateArray())
            result.Add(new RagChunk(
                Id: Str(chunk, "chunk_id", "id"),
                Content: Str(chunk, "content", "content_with_weight"),
                DocumentId: Str(chunk, "document_id", "doc_id"),
                DocumentName: Str(chunk, "document_name", "docnm_kwd"),
                ImageId: chunk.TryGetProperty("image_id", out var img) &&
                         img.ValueKind == JsonValueKind.String ? img.GetString() : null,
                Similarity: chunk.TryGetProperty("similarity", out var s) &&
                            s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0.0
            ));
        return result;
    }

    // ── 7a. Retrieve + generate without persisting — used by Evaluate ─────────
    public async Task<string> GetAnswerAsync(
        string datasetId,
        string question,
        DeptFilter? filter = null)
    {
        var chunks = await _retriever.RetrieveAsync(
            question, datasetId,
            bm25Weight: _bm25Weight,
            filter: filter);

        var answer = await CallGeminiForAnswerAsync(
            question, chunks.Select(c => c.Content).ToList());

        return BuildCompletionJson(answer, chunks);
    }

    // ── 7b. Ask a question, persist Q&A to ConversationStore ─────────────────
    public async Task<string> AskQuestionAsync(
        string datasetId, string sessionId, string question,
        DeptFilter? filter = null)
    {
        var completionJson = await GetAnswerAsync(datasetId, question, filter);

        // Deserialise the answer and chunks back out so we can persist them
        var answer = ExtractAnswerFromJson(completionJson);
        var chunks = ParseCompletionChunks(completionJson);

        var cacheKey = $"messages:{sessionId}";
        if (!_cache.TryGetValue(cacheKey, out List<ChatMessage>? history) || history == null)
            history = await _store.LoadAsync(sessionId) ?? [];

        history.Add(new ChatMessage("user", question,
            StableMessageId("user", question + history.Count)));
        var aiId = StableMessageId("assistant", answer + history.Count);
        history.Add(new ChatMessage("assistant", answer, aiId) { Chunks = chunks });

        _cache.Set(cacheKey, history, TimeSpan.FromHours(24));
        await _store.SaveAsync(sessionId, history);

        return completionJson;
    }

    internal static string ExtractAnswerFromJson(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            return doc.RootElement.GetProperty("data").GetProperty("answer").GetString() ?? "";
        }
        catch { return ""; }
    }

    public async Task<string> CallGeminiForAnswerAsync(string question, List<string> contexts)
    {
        var contextBlock = contexts.Count > 0
            ? string.Join("\n\n", contexts.Select((c, i) => $"[ID:{i}] {c}"))
            : "(no relevant context found)";

        var prompt = $"""
            You are a helpful assistant. Answer the question using ONLY the provided context.
            If the context does not contain the answer, say so clearly.

            Each context snippet is labelled [ID:N] where N is a 0-based index.
            Whenever you use information from a snippet, cite it inline using exactly [ID:N]
            (e.g. "The policy states [ID:0] that…"). Use the label exactly as shown; do not
            change the format (no parentheses, no "Reference", no other wording).

            Context:
            {contextBlock}

            Question: {question}

            Answer:
            """;

        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 8192 }
        };

        try
        {
            var http = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{GeminiAnswerUrl}?key={_geminiApiKey}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Gemini answer call returned {Code}: {Body}", (int)response.StatusCode, body);
                return $"(Gemini error {(int)response.StatusCode}: {body})";
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "(empty response)";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gemini answer call failed");
            return $"(error: {ex.Message})";
        }
    }

    private static string BuildCompletionJson(string answer, List<RagChunk> chunks)
    {
        var obj = new
        {
            code = 0,
            data = new
            {
                answer,
                reference = new
                {
                    chunks = chunks.Select(c => new
                    {
                        chunk_id = c.Id,
                        content = c.Content,
                        document_id = c.DocumentId,
                        document_name = c.DocumentName,
                        image_id = c.ImageId,
                        similarity = c.Similarity
                    })
                }
            }
        };
        return JsonSerializer.Serialize(obj);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static StringContent ToJson(object obj) =>
        new(JsonSerializer.Serialize(obj, _json), Encoding.UTF8, "application/json");

    // Produces a stable 12-char hex ID from role+content when RagFlow omits one.
    internal static string StableMessageId(string role, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(role + content));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            _log.LogWarning("RAGFlow returned {Code}: {Body}", (int)res.StatusCode, body);
        return body;
    }
    // ── List all documents in a dataset ────────────────────────────────────────
    public async Task<List<DocumentItem>> ListDocumentsAsync(
        string datasetId, int page = 1, int pageSize = 100)
    {
        var req = NewRequest(HttpMethod.Get,
            $"/api/v1/datasets/{datasetId}/documents?page={page}&page_size={pageSize}");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        var list = new List<DocumentItem>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var docs = doc.RootElement.GetProperty("data").GetProperty("docs");
            foreach (var d in docs.EnumerateArray())
            {
                list.Add(new DocumentItem(
                    Id: d.GetProperty("id").GetString() ?? "",
                    Name: d.GetProperty("name").GetString() ?? "",
                    Size: NumberOrZero(d, "size"),
                    Type: d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    RunStatus: d.TryGetProperty("run", out var r) ? r.GetString() ?? "UNSTART" : "UNSTART",
                    ChunkCount: (int)NumberOrZero(d, "chunk_count"),
                    TokenCount: (int)NumberOrZero(d, "token_count"),
                    Progress: d.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
                                    ? p.GetDouble() : 0.0,
                    CreateTime: NumberOrZero(d, "create_time")
                ));
            }
        }
        catch { /* swallow — empty list is fine */ }
        return list;

        static long NumberOrZero(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64() : 0;
    }
    // ── Delete one or more documents ───────────────────────────────────────────
    public async Task DeleteDocumentsAsync(string datasetId, IEnumerable<string> documentIds)
    {
        var ids = documentIds?.ToArray() ?? [];
        if (ids.Length == 0) return;

        var req = NewRequest(HttpMethod.Delete, $"/api/v1/datasets/{datasetId}/documents");
        req.Content = ToJson(new { ids });
        await _http.SendAsync(req);
    }
    // ── Fetch a chunk page image (proxied to browser) ──────────────────────────
    // Tries multiple endpoint formats and handles JSON-wrapped base64 responses,
    // which some RAGFlow versions return instead of raw image bytes.
    public async Task<(byte[] Data, string ContentType)> GetChunkImageAsync(string imageId)
    {
        // RAGFlow has used different paths across versions — try both
        var candidates = new[]
        {
            $"/api/v1/document/image/{imageId}",
            $"/v1/document/image/{imageId}",
        };

        foreach (var path in candidates)
        {
            try
            {
                var req = NewRequest(HttpMethod.Get, path);
                var res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;

                var ct = res.Content.Headers.ContentType?.MediaType ?? "";
                var data = await res.Content.ReadAsByteArrayAsync();

                // ── Case 1: raw image bytes ───────────────────────────────
                if (ct.StartsWith("image/"))
                    return (data, ct);

                // ── Case 2: JSON-wrapped response e.g. {"image":"base64..."} ─
                // Some RAGFlow builds return {"data":"<base64>","content_type":"image/png"}
                if (ct.Contains("json") || (data.Length > 0 && data[0] == (byte)'{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        // Try common field names
                        string? b64 = null;
                        string? imageCt = "image/png";
                        foreach (var field in new[] { "image", "data", "content", "result" })
                            if (root.TryGetProperty(field, out var v) &&
                                v.ValueKind == JsonValueKind.String)
                            { b64 = v.GetString(); break; }

                        if (root.TryGetProperty("content_type", out var ctProp))
                            imageCt = ctProp.GetString() ?? imageCt;

                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            // Strip data URI prefix if present: data:image/png;base64,...
                            var comma = b64!.IndexOf(',');
                            if (comma >= 0) b64 = b64[(comma + 1)..];
                            return (Convert.FromBase64String(b64), imageCt!);
                        }
                    }
                    catch { /* not JSON or unexpected shape — fall through */ }
                }

                // ── Case 3: raw bytes but wrong Content-Type header ───────
                // Treat as image if it starts with known magic bytes
                if (data.Length >= 4 && IsImageMagic(data))
                    return (data, InferContentType(data));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Image fetch failed for path {Path}: {Msg}", path, ex.Message);
            }
        }

        // Nothing worked — return a small grey placeholder so the <img> doesn't break layout
        _log.LogWarning("Could not fetch chunk image {Id} from any known endpoint", imageId);
        return (PlaceholderPng(), "image/png");
    }
    
    private static bool IsImageMagic(byte[] b) =>
        (b[0] == 0xFF && b[1] == 0xD8)                   // JPEG
        || (b[0] == 0x89 && b[1] == 0x50)                // PNG
        || (b[0] == 0x47 && b[1] == 0x49)                // GIF
        || (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46); // WEBP

    private static string InferContentType(byte[] b) =>
        (b[0] == 0xFF && b[1] == 0xD8) ? "image/jpeg" :
        (b[0] == 0x89 && b[1] == 0x50) ? "image/png" :
        (b[0] == 0x47 && b[1] == 0x49) ? "image/gif" : "image/webp";

    // 1×1 grey PNG used when the real image cannot be retrieved
    private static byte[] PlaceholderPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // ── List chunks for a single document ──────────────────────────────────────
    public async Task<List<DocumentChunk>> ListChunksAsync(
        string datasetId, string documentId, int page = 1, int pageSize = 100)
    {
        var req = NewRequest(HttpMethod.Get,
            $"/api/v1/datasets/{datasetId}/documents/{documentId}/chunks?page={page}&page_size={pageSize}");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        var list = new List<DocumentChunk>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var chunks = doc.RootElement.GetProperty("data").GetProperty("chunks");
            foreach (var c in chunks.EnumerateArray())
            {
                // RAGFlow v0.26 uses "chunk_id"; older versions used "id"
                var chunkId =
                    c.TryGetProperty("chunk_id", out var cidEl) ? cidEl.GetString() ?? "" :
                    c.TryGetProperty("id",       out var idEl)  ? idEl.GetString()  ?? "" : "";

                var content =
                    c.TryGetProperty("content",              out var ctEl) ? ctEl.GetString() ?? "" :
                    c.TryGetProperty("content_with_weight",  out var cwEl) ? cwEl.GetString() ?? "" : "";

                // available_int=1 (int) or available=true (bool); absent → treat as available
                bool available = true;
                if (c.TryGetProperty("available_int", out var avInt))
                    available = avInt.ValueKind == JsonValueKind.Number && avInt.GetInt32() != 0;
                else if (c.TryGetProperty("available", out var avBool))
                    available = avBool.ValueKind != JsonValueKind.False;

                list.Add(new DocumentChunk(
                    Id: chunkId,
                    Content: content,
                    DocumentId: documentId,
                    Available: available,
                    ImageId: c.TryGetProperty("image_id", out var img) && img.ValueKind == JsonValueKind.String
                                    ? img.GetString() : null,
                    Keywords: c.TryGetProperty("important_keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
                                    ? [.. kw.EnumerateArray().Select(k => k.GetString() ?? "")]
                                    : []
                ));
            }
        }
        catch { /* empty list on parse failure */ }
        return list;
    }
    // ── Add a single pre-built chunk to a document ────────────────────────────
    public async Task AddChunkAsync(string datasetId, string documentId, Models.IngestionChunk chunk)
    {
        var req = NewRequest(HttpMethod.Post,
            $"/api/v1/datasets/{datasetId}/documents/{documentId}/chunks");
        req.Content = ToJson(new
        {
            content = chunk.Content,
            important_keywords = chunk.Keywords ?? []
        });
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString() : body;
            throw new InvalidOperationException($"RAGFlow rejected chunk: {msg}");
        }
    }

    public async Task UpdateChunkAsync(
    string datasetId, string documentId, string chunkId, string newContent)
    {
        var req = NewRequest(HttpMethod.Put,
            $"/api/v1/datasets/{datasetId}/documents/{documentId}/chunks/{chunkId}");
        req.Content = ToJson(new { content = newContent });
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m)
                ? m.GetString() : body;
            throw new InvalidOperationException($"RAGFlow rejected chunk update: {msg}");
        }
    }

    // ── System info: active embedding model from first dataset ─────────────────
    public async Task<(string EmbeddingModel, string LayoutRecognize, string LlmId)>
        GetActiveModelsFromDatasetAsync()
    {
        var req = NewRequest(HttpMethod.Get, "/api/v1/datasets?page=1&page_size=1");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
            var cfg = first.GetProperty("parser_config");

            var embedding = first.TryGetProperty("embedding_model", out var e)
                ? e.GetString() ?? "unknown" : "unknown";
            var layout = cfg.TryGetProperty("layout_recognize", out var l)
                ? l.GetString() ?? "unknown" : "unknown";
            var llm = cfg.TryGetProperty("llm_id", out var ll)
                ? ll.GetString() ?? "unknown" : "unknown";

            return (embedding, layout, llm);
        }
        catch { return ("unknown", "unknown", "unknown"); }
    }
    /// <summary>
    /// Creates a new personal RAGFlow dataset for a user at registration time.
    /// The dataset is configured to match the existing system settings:
    /// naive chunking at 512 tokens with 15 % overlap, DeepDOC layout recognition,
    /// and bge-m3 embeddings via Ollama.
    /// </summary>
    /// <param name="ownerUsername">
    /// Used as the dataset name so the RAGFlow dashboard stays readable.
    /// </param>
    /// <returns>
    /// The RAGFlow-assigned dataset ID, or throws if creation failed.
    /// </returns>
    public async Task<string> CreateDatasetAsync(string ownerUsername)
    {
        var req = NewRequest(HttpMethod.Post, "/api/v1/datasets");
        req.Content = ToJson(new
        {
            name = $"{ownerUsername}_dataset",
            embedding_model = _embeddingModel,
            permission = "me",
            chunk_method = "naive",
            parser_config = new
            {
                chunk_token_num = 512,
                delimiter = "\n",
                layout_recognize = "DeepDOC"
            }
        });

        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement
                .GetProperty("data")
                .GetProperty("id")
                .GetString();

            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    "RAGFlow returned an empty dataset ID. Body: " + body);

            _log.LogInformation(
                "Created dataset for user '{User}': {DatasetId}", ownerUsername, id);

            return id!;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Failed to parse dataset creation response. Body: " + body, ex);
        }
    }
    private async Task<string?> FindAssistantByNameAsync(string name)
    {
        var req = NewRequest(HttpMethod.Get,
            $"/api/v1/chats?name={Uri.EscapeDataString(name)}");
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var chat in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                if (chat.TryGetProperty("name", out var n) &&
                    n.GetString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
                    chat.TryGetProperty("id", out var idEl))
                    return idEl.GetString();
            }
        }
        catch { /* return null below */ }

        return null;
    }

    /// <summary>
    /// Creates a new personal RAGFlow assistant (chat agent) for a user
    /// at registration time, bound exclusively to their personal dataset.
    /// The assistant is configured with the same retrieval parameters
    /// (similarity threshold 0.2, BM25/vector split 0.7/0.3) as the
    /// existing shared assistant.
    /// </summary>
    /// <param name="ownerUsername">
    /// Used as the assistant name so the RAGFlow dashboard stays readable.
    /// </param>
    /// <param name="datasetId">
    /// The dataset ID returned by <see cref="CreateDatasetAsync"/>.
    /// The assistant will only search this dataset at query time.
    /// </param>
    /// <returns>
    /// The RAGFlow-assigned assistant ID, or throws if creation failed.
    /// </returns>
    public async Task<string> CreateAssistantAsync(string ownerUsername)
    {
        var req = NewRequest(HttpMethod.Post, "/api/v1/chats");
        req.Content = ToJson(new
        {
            name = $"{ownerUsername}_assistant",
            prompt = new
            {
                similarity_threshold = 0.2,
                keywords_similarity_weight = 0.3,
                top_n = 8,
                empty_response = "Sorry, no relevant information was found in the knowledge base.",
                opener = "Hi! I'm your RAG assistant. What would you like to know?",
                show_quote = true
            }
        });

        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Check RAGFlow application-level result before touching "data"
            if (root.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? "" : "";

                // Duplicate name — recover the existing assistant's ID instead of failing
                if (msg.Contains("Duplicated", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning(
                        "Assistant '{Name}_assistant' already exists — recovering ID.",
                        ownerUsername);
                    return await FindAssistantByNameAsync($"{ownerUsername}_assistant")
                        ?? throw new InvalidOperationException(
                            $"Duplicate assistant exists but could not be found by name.");
                }

                throw new InvalidOperationException(
                    $"RAGFlow rejected assistant creation (code {codeEl.GetInt32()}): {msg}");
            }

            var id = root.GetProperty("data").GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException(
                    "RAGFlow returned an empty assistant ID. Body: " + body);

            _log.LogInformation(
                "Created assistant for user '{User}': {AssistantId}", ownerUsername, id);
            return id!;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Failed to parse assistant creation response. Body: " + body, ex);
        }
    }
    /// <summary>
    /// Binds a dataset to an existing assistant via PUT /api/v1/chats/{assistantId}.
    /// Called by UserContext after the first document is successfully uploaded
    /// and the dataset has at least one parsed file.
    /// </summary>
    /// <param name="assistantId">The assistant to update.</param>
    /// <param name="datasetId">The dataset to bind to the assistant.</param>
    public async Task BindDatasetToAssistantAsync(string assistantId, string datasetId)
    {
        var req = NewRequest(HttpMethod.Put, $"/api/v1/chats/{assistantId}");
        req.Content = ToJson(new
        {
            dataset_ids = new[] { datasetId }
        });
        var res = await _http.SendAsync(req);
        var body = await ReadBodyAsync(res);
        _log.LogInformation(
            "Bound dataset {DS} to assistant {AS}. Response: {Body}",
            datasetId, assistantId, body);
    }
    /// <summary>
    /// Uploads a document to a dataset from a raw byte array.
    /// Used by the reparse flow where the source is a cached file rather
    /// than an incoming IFormFile.
    /// </summary>
    public async Task<string> UploadDocumentBytesAsync(
        string datasetId, byte[] bytes, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);

        var req = NewRequest(HttpMethod.Post,
            $"/api/v1/datasets/{datasetId}/documents");
        req.Content = content;

        var res = await _http.SendAsync(req);
        return await ReadBodyAsync(res);
    }

    public async Task<string> ListDatasetsAsync(int page = 1, int pageSize = 10)
    {
        var req = NewRequest(HttpMethod.Get,
            $"/api/v1/datasets?page={page}&page_size={pageSize}");
        var res = await _http.SendAsync(req);
        return await ReadBodyAsync(res);
    }
    // ── Rename a chat session ─────────────────────────────────────────────────────
    public async Task RenameSessionAsync(string assistantId, string sessionId, string newName)
    {
        var req = NewRequest(HttpMethod.Put,
            $"/api/v1/chats/{assistantId}/sessions/{sessionId}");
        req.Content = ToJson(new { name = newName });
        var res = await _http.SendAsync(req);
        await ReadBodyAsync(res);
    }
}