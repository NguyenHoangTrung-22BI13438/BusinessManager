using Dapper;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class PendingDocumentStore
{
    private readonly AppDbContext _db;
    private readonly string _fileDir;

    public PendingDocumentStore(AppDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _fileDir = Path.Combine(env.ContentRootPath, "pending-docs");
        Directory.CreateDirectory(_fileDir);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<List<PendingDocument>> GetAllAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<PendingRow>(
            "SELECT * FROM pending_documents ORDER BY submitted_at DESC");
        return rows.Select(ToDoc).ToList();
    }

    public async Task<List<PendingDocument>> GetByUserAsync(string username)
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<PendingRow>(
            "SELECT * FROM pending_documents WHERE submitted_by = @u ORDER BY submitted_at DESC",
            new { u = username });
        return rows.Select(ToDoc).ToList();
    }

    public async Task<List<PendingDocument>> GetPendingAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<PendingRow>(
            "SELECT * FROM pending_documents WHERE status = 'Pending' ORDER BY submitted_at");
        return rows.Select(ToDoc).ToList();
    }

    public async Task<int> GetPendingCountAsync()
    {
        await using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM pending_documents WHERE status = 'Pending'");
    }

    public async Task<PendingDocument?> GetByIdAsync(string id)
    {
        await using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<PendingRow>(
            "SELECT * FROM pending_documents WHERE id = @id", new { id });
        return row is null ? null : ToDoc(row);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<PendingDocument> AddAsync(string username, IFormFile file)
    {
        var ext      = Path.GetExtension(file.FileName);
        var filePath = Path.Combine(_fileDir, Guid.NewGuid().ToString("N") + ext);

        using (var fs = File.Create(filePath))
            await file.CopyToAsync(fs);

        var doc = new PendingDocument
        {
            FileName    = file.FileName,
            ContentType = file.ContentType ?? "application/octet-stream",
            UploadedBy  = username,
            FilePath    = filePath
        };

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO pending_documents
                (id, submitted_by, file_name, content_type, storage_key, file_path, submitted_at, status)
            VALUES
                (@Id, @UploadedBy, @FileName, @ContentType, '', @FilePath, UTC_TIMESTAMP(), 'Pending')",
            new { doc.Id, doc.UploadedBy, doc.FileName, doc.ContentType, doc.FilePath });

        return doc;
    }

    public async Task UpdateStatusAsync(string id, PendingStatus status)
    {
        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE pending_documents SET status = @s WHERE id = @id",
            new { s = status.ToString(), id });
    }

    public void DeleteFile(PendingDocument doc)
    {
        if (File.Exists(doc.FilePath)) File.Delete(doc.FilePath);
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private sealed class PendingRow
    {
        public string   id           { get; set; } = "";
        public string   submitted_by { get; set; } = "";
        public string   file_name    { get; set; } = "";
        public string   content_type { get; set; } = "application/octet-stream";
        public string   storage_key  { get; set; } = "";
        public string   file_path    { get; set; } = "";
        public DateTime submitted_at { get; set; }
        public string   status       { get; set; } = "Pending";
    }

    private static PendingDocument ToDoc(PendingRow r) =>
        new()
        {
            Id          = r.id,
            FileName    = r.file_name,
            ContentType = r.content_type,
            UploadedBy  = r.submitted_by,
            UploadedAt  = r.submitted_at,
            FilePath    = r.file_path,
            Status      = Enum.TryParse<PendingStatus>(r.status, out var s) ? s : PendingStatus.Pending
        };
}
