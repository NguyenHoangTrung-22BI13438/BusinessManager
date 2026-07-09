using Dapper;
using RagFlowApi.Models;
using System.Text.Json;

namespace RagFlowApi.Services;

public class FormLibraryStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<FormLibraryStore> _log;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FormLibraryStore(AppDbContext db, ILogger<FormLibraryStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<List<FormTemplate>> GetAllAsync()
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var rows = await conn.QueryAsync<TemplateRow>(
                "SELECT id, name, file_name, uploaded_by, fields_json, created_at FROM form_templates ORDER BY created_at DESC");
            return rows.Select(ToTemplate).ToList();
        }
        catch (Exception ex) { _log.LogError(ex, "GetAllAsync failed"); return []; }
    }

    public async Task<FormTemplate?> GetByIdAsync(string id)
    {
        try
        {
            await using var conn = _db.CreateConnection();
            var row = await conn.QueryFirstOrDefaultAsync<TemplateRow>(
                "SELECT id, name, file_name, uploaded_by, fields_json, created_at FROM form_templates WHERE id = @id",
                new { id });
            return row is null ? null : ToTemplate(row);
        }
        catch (Exception ex) { _log.LogError(ex, "GetByIdAsync failed"); return null; }
    }

    public async Task<string> AddAsync(
        string name, string fileName, byte[] bytes,
        List<FormField> fields, string uploadedBy)
    {
        var id         = Guid.NewGuid().ToString("N");
        var fieldsJson = JsonSerializer.Serialize(fields, _opts);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO form_templates
                (id, name, file_name, uploaded_by, fields_json, docx_data, created_at)
            VALUES
                (@id, @name, @fileName, @uploadedBy, @fieldsJson, @bytes, UTC_TIMESTAMP())",
            new { id, name, fileName, uploadedBy, fieldsJson, bytes });

        return id;
    }

    public async Task DeleteAsync(string id)
    {
        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM form_templates WHERE id = @id", new { id });
    }

    public byte[]? GetBytes(string id)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        return conn.QueryFirstOrDefault<byte[]?>(
            "SELECT docx_data FROM form_templates WHERE id = @id", new { id });
    }

    public string? GetFileName(string id)
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        return conn.QueryFirstOrDefault<string?>(
            "SELECT file_name FROM form_templates WHERE id = @id", new { id });
    }

    private static FormTemplate ToTemplate(TemplateRow r)
    {
        var fields = string.IsNullOrEmpty(r.fields_json)
            ? (List<FormField>)[]
            : JsonSerializer.Deserialize<List<FormField>>(r.fields_json, _opts) ?? [];

        return new FormTemplate
        {
            Id         = r.id,
            Name       = r.name,
            FileName   = r.file_name,
            UploadedBy = r.uploaded_by,
            UploadedAt = r.created_at,
            Fields     = fields
        };
    }

    private sealed class TemplateRow
    {
        public string   id          { get; set; } = "";
        public string   name        { get; set; } = "";
        public string   file_name   { get; set; } = "";
        public string   uploaded_by { get; set; } = "";
        public string?  fields_json { get; set; }
        public DateTime created_at  { get; set; }
    }
}
