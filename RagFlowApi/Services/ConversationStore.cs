using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// File-backed conversation store so chat history survives app restarts.
/// The Gemini path never posts to RAGFlow's session API, so IMemoryCache alone
/// loses all history when the process exits.
/// </summary>
public class ConversationStore
{
    private readonly string _dir;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConversationStore(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "conversations");
        Directory.CreateDirectory(_dir);
    }

    public async Task SaveAsync(string sessionId, List<ChatMessage> messages)
    {
        var path = Path.Combine(_dir, $"{sessionId}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(messages, _json));
    }

    public async Task<List<ChatMessage>?> LoadAsync(string sessionId)
    {
        var path = Path.Combine(_dir, $"{sessionId}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<ChatMessage>>(json, _json);
        }
        catch { return null; }
    }
}
