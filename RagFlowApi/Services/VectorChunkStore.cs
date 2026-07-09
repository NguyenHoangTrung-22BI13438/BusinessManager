using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Thread-safe in-memory store of chunk embeddings, persisted to a JSON file
/// on disk so it survives restarts.  Keyed on chunk ID (a stable content hash).
///
/// File size: ~10 KB per chunk (1024-float BGE-M3 embedding serialised as JSON).
/// 500 chunks ≈ 5 MB — acceptable for a research-scale deployment.
/// </summary>
public class VectorChunkStore
{
    private readonly Dictionary<string, StoredChunk> _store = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private readonly ILogger<VectorChunkStore> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VectorChunkStore(IWebHostEnvironment env, ILogger<VectorChunkStore> log)
    {
        _log  = log;
        _path = Path.Combine(env.ContentRootPath, "vector_store.json");
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<StoredChunk>>(
                File.ReadAllText(_path), _json) ?? [];
            foreach (var c in list) _store[c.Id] = c;
            _log.LogInformation("Loaded {N} chunks from vector store", _store.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load vector store from {Path}", _path);
        }
    }

    public async Task AddRangeAsync(IEnumerable<StoredChunk> chunks)
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var c in chunks) _store[c.Id] = c;
            await PersistAsync();
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteByDocumentAsync(string documentId)
    {
        await _lock.WaitAsync();
        try
        {
            var ids = _store.Values
                .Where(c => c.DocumentId == documentId)
                .Select(c => c.Id)
                .ToList();
            foreach (var id in ids) _store.Remove(id);
            await PersistAsync();
            _log.LogInformation(
                "Removed {N} chunks for document {Id}", ids.Count, documentId);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns a snapshot of all chunks across all datasets.</summary>
    public async Task<List<StoredChunk>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try { return [.. _store.Values]; }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Returns chunks for a dataset, filtered to the allowed categories.
    /// Pass null (admin) to return all categories.
    /// </summary>
    public async Task<List<StoredChunk>> GetByDatasetAsync(
        string datasetId,
        IReadOnlyList<string>? allowedCategories = null)
    {
        await _lock.WaitAsync();
        try
        {
            var q = _store.Values.Where(c => c.DatasetId == datasetId);
            if (allowedCategories is { Count: > 0 })
                q = q.Where(c => allowedCategories.Contains(c.Category, StringComparer.OrdinalIgnoreCase));
            return q.ToList();
        }
        finally { _lock.Release(); }
    }

    /// <summary>Returns all chunks for a specific document (used to preserve category on reparse).</summary>
    public async Task<List<StoredChunk>> GetByDocumentAsync(string documentId)
    {
        await _lock.WaitAsync();
        try { return _store.Values.Where(c => c.DocumentId == documentId).ToList(); }
        finally { _lock.Release(); }
    }

    /// <summary>Returns the distinct categories present in the store (for the upload dropdown).</summary>
    public async Task<List<string>> GetCategoriesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _store.Values
                .Select(c => c.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .ToList();
        }
        finally { _lock.Release(); }
    }

    private Task PersistAsync() =>
        File.WriteAllTextAsync(
            _path,
            JsonSerializer.Serialize(_store.Values.ToList(), _json));
}
