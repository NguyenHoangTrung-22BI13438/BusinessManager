using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class PendingDocumentStore
{
    private readonly string _jsonPath;
    private readonly string _fileDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions _writeOpts = new() { WriteIndented = true };

    public PendingDocumentStore(IWebHostEnvironment env)
    {
        _jsonPath = Path.Combine(env.ContentRootPath, "pending.json");
        _fileDir = Path.Combine(env.ContentRootPath, "pending-docs");
        Directory.CreateDirectory(_fileDir);
        if (!File.Exists(_jsonPath)) File.WriteAllText(_jsonPath, "[]");
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<List<PendingDocument>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_jsonPath);
            return JsonSerializer.Deserialize<List<PendingDocument>>(json) ?? [];
        }
        finally { _lock.Release(); }
    }

    public async Task<List<PendingDocument>> GetByUserAsync(string username)
    {
        var all = await GetAllAsync();
        return [.. all.Where(d => d.UploadedBy.Equals(
            username, StringComparison.OrdinalIgnoreCase))];
    }

    public async Task<List<PendingDocument>> GetPendingAsync()
    {
        var all = await GetAllAsync();
        return [.. all.Where(d => d.Status == PendingStatus.Pending)];
    }

    public async Task<int> GetPendingCountAsync()
        => (await GetPendingAsync()).Count;

    public async Task<PendingDocument?> GetByIdAsync(string id)
    {
        var all = await GetAllAsync();
        return all.FirstOrDefault(d => d.Id == id);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<PendingDocument> AddAsync(
        string username, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName);
        var doc = new PendingDocument
        {
            FileName = file.FileName,
            ContentType = file.ContentType ?? "application/octet-stream",
            UploadedBy = username,
            FilePath = Path.Combine(_fileDir, Guid.NewGuid().ToString("N") + ext)
        };

        // Persist bytes before touching the JSON
        using (var fs = File.Create(doc.FilePath))
            await file.CopyToAsync(fs);

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_jsonPath);
            var all = JsonSerializer.Deserialize<List<PendingDocument>>(json) ?? [];
            all.Add(doc);
            await File.WriteAllTextAsync(_jsonPath,
                JsonSerializer.Serialize(all, _writeOpts));
        }
        finally { _lock.Release(); }

        return doc;
    }

    public async Task UpdateStatusAsync(string id, PendingStatus status)
    {
        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_jsonPath);
            var all = JsonSerializer.Deserialize<List<PendingDocument>>(json) ?? [];
            var doc = all.FirstOrDefault(d => d.Id == id);
            if (doc is null) return;
            doc.Status = status;
            await File.WriteAllTextAsync(_jsonPath,
                JsonSerializer.Serialize(all, _writeOpts));
        }
        finally { _lock.Release(); }
    }

    public void DeleteFile(PendingDocument doc)
    {
        if (File.Exists(doc.FilePath)) File.Delete(doc.FilePath);
    }
}