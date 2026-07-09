using Dapper;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class RatingStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<RatingStore> _log;

    public RatingStore(AppDbContext db, ILogger<RatingStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task AddOrUpdateAsync(RatingEntry entry)
    {
        try
        {
            await using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO ratings (session_id, message_id, username, positive, rated_at)
                VALUES (@SessionId, @MessageId, @Username, @Positive, @Timestamp)
                ON DUPLICATE KEY UPDATE positive = @Positive, rated_at = @Timestamp",
                new
                {
                    entry.SessionId,
                    entry.MessageId,
                    entry.Username,
                    entry.Positive,
                    entry.Timestamp
                });
        }
        catch (Exception ex) { _log.LogError(ex, "AddOrUpdateAsync failed"); }
    }

    public async Task<Dictionary<string, bool>> GetRatingsForUserInSessionAsync(
        string username, string sessionId)
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<RatingRow>(
            "SELECT message_id, positive FROM ratings WHERE username = @u AND session_id = @sid",
            new { u = username, sid = sessionId });
        return rows.ToDictionary(r => r.message_id, r => r.positive);
    }

    public async Task<List<RatingEntry>> GetAllAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<RatingRow>(
            "SELECT * FROM ratings ORDER BY rated_at");
        return rows.Select(r =>
            new RatingEntry(r.session_id, r.message_id, r.username, r.positive, r.rated_at)
        ).ToList();
    }

    private sealed class RatingRow
    {
        public string   session_id { get; set; } = "";
        public string   message_id { get; set; } = "";
        public string   username   { get; set; } = "";
        public bool     positive   { get; set; }
        public DateTime rated_at   { get; set; }
    }
}
