using System.Text.Json;

namespace RagFlowApi.Services;

/// <summary>
/// Persistent cache that maps a file's SHA-256 hash to its detected+suggested fields.
/// Same file uploaded again → instant result, no re-detection or AI call.
/// Stored in form_cache.json alongside users.json/ratings.json.
/// </summary>
public class FormTemplateCache
{
    private readonly string _path;
    private readonly SemaphoreSlim _sem = new(1, 1);
    private Dictionary<string, CacheEntry> _data = new();

    private record CacheEntry(List<FormField> Fields, DateTime CachedAt);

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented    = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FormTemplateCache(IWebHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, "form_cache.json");
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            _data = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, _opts) ?? new();
        }
        catch { _data = new(); }
    }

    /// <summary>Returns cached fields for the given file hash, or null on miss.</summary>
    public async Task<List<FormField>?> GetAsync(string hash)
    {
        await _sem.WaitAsync();
        try { return _data.TryGetValue(hash, out var e) ? e.Fields : null; }
        finally { _sem.Release(); }
    }

    /// <summary>Stores detected+suggested fields under the given file hash.</summary>
    public async Task SetAsync(string hash, List<FormField> fields)
    {
        await _sem.WaitAsync();
        try
        {
            _data[hash] = new CacheEntry(fields, DateTime.UtcNow);
            Prune();
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(_data, _opts));
        }
        finally { _sem.Release(); }
    }

    // Keep at most 500 entries; evict the oldest when over the limit.
    private void Prune()
    {
        if (_data.Count <= 500) return;
        var toRemove = _data.OrderBy(kv => kv.Value.CachedAt)
                            .Take(_data.Count - 500)
                            .Select(kv => kv.Key)
                            .ToList();
        foreach (var k in toRemove) _data.Remove(k);
    }
}
