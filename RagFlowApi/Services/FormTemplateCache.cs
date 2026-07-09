using Dapper;
using System.Text.Json;

namespace RagFlowApi.Services;

public class FormTemplateCache
{
    private readonly AppDbContext _db;

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FormTemplateCache(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<FormField>?> GetAsync(string hash)
    {
        await using var conn = _db.CreateConnection();
        var json = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT fields_json FROM form_field_cache WHERE file_hash = @h",
            new { h = hash });

        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<List<FormField>>(json, _opts); }
        catch { return null; }
    }

    public async Task SetAsync(string hash, List<FormField> fields)
    {
        await using var conn = _db.CreateConnection();
        var json = JsonSerializer.Serialize(fields, _opts);

        await conn.ExecuteAsync(@"
            INSERT INTO form_field_cache (file_hash, fields_json, cached_at)
            VALUES (@h, @j, datetime('now'))
            ON DUPLICATE KEY UPDATE fields_json = @j, cached_at = UTC_TIMESTAMP()",
            new { h = hash, j = json });

        // Keep at most 500 entries; evict the oldest
        await conn.ExecuteAsync(@"
            DELETE FROM form_field_cache
            WHERE file_hash NOT IN (
                SELECT file_hash FROM (
                    SELECT file_hash FROM form_field_cache
                    ORDER BY cached_at DESC LIMIT 500
                ) sub
            )");
    }
}
