using Dapper;
using RagFlowApi.Models;
using System.Text.Json;

namespace RagFlowApi.Services;

public class ConversationStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<ConversationStore> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ConversationStore(AppDbContext db, ILogger<ConversationStore> log)
    {
        _db  = db;
        _log = log;
    }

    // ── Conversation history ──────────────────────────────────────────────────

    public async Task SaveAsync(string sessionId, List<ChatMessage> messages)
    {
        await using var conn = _db.CreateConnection();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM messages WHERE session_id = @sid",
                new { sid = sessionId }, tx);

            foreach (var msg in messages)
            {
                var chunksJson = msg.Chunks.Count > 0
                    ? JsonSerializer.Serialize(msg.Chunks)
                    : null;

                await conn.ExecuteAsync(@"
                    INSERT INTO messages (id, session_id, role, content, chunks_json, created_at)
                    VALUES (@Id, @Sid, @Role, @Content, @Chunks, UTC_TIMESTAMP())",
                    new { msg.Id, Sid = sessionId, msg.Role, msg.Content, Chunks = chunksJson },
                    tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<List<ChatMessage>?> LoadAsync(string sessionId)
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var rows = await conn.QueryAsync<MessageRow>(
                "SELECT * FROM messages WHERE session_id = @sid ORDER BY created_at",
                new { sid = sessionId });

            var list = rows.ToList();
            if (list.Count == 0) return null;

            return list.Select(r =>
            {
                var chunks = string.IsNullOrEmpty(r.chunks_json)
                    ? (List<RagChunk>)[]
                    : JsonSerializer.Deserialize<List<RagChunk>>(r.chunks_json, _json) ?? [];
                return new ChatMessage(r.role, r.content, r.id) { Chunks = chunks };
            }).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LoadAsync failed for session {Id}", sessionId);
            return null;
        }
    }

    // ── Session index ─────────────────────────────────────────────────────────

    public async Task<List<SessionItem>> ListByUsernameAsync(string username)
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<(string id, string name)>(
            "SELECT id, name FROM sessions WHERE username = @u ORDER BY created_at DESC",
            new { u = username });
        return rows.Select(r => new SessionItem(r.id, r.name)).ToList();
    }

    public async Task<string> CreateSessionAsync(string username, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO sessions (id, name, username, created_at) VALUES (@id, @name, @username, UTC_TIMESTAMP())",
            new { id, name, username });
        return id;
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await using var conn = _db.CreateConnection();
        // CASCADE in the FK deletes messages automatically
        await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE id = @sid",
            new { sid = sessionId });
    }

    public async Task RenameSessionAsync(string sessionId, string newName)
    {
        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE sessions SET name = @name WHERE id = @sid",
            new { name = newName, sid = sessionId });
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private sealed class MessageRow
    {
        public string  id          { get; set; } = "";
        public string  session_id  { get; set; } = "";
        public string  role        { get; set; } = "";
        public string  content     { get; set; } = "";
        public string? chunks_json { get; set; }
        public DateTime created_at { get; set; }
    }
}
