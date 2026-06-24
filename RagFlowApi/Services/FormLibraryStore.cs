using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class FormLibraryStore
{
    private readonly string _metaPath;
    private readonly string _bytesDir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<FormLibraryStore> _log;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FormLibraryStore(IWebHostEnvironment env, ILogger<FormLibraryStore> log)
    {
        _log = log;
        _metaPath = Path.Combine(env.ContentRootPath, "form_library.json");
        _bytesDir = Path.Combine(env.ContentRootPath, "form-library");
        Directory.CreateDirectory(_bytesDir);
        if (!File.Exists(_metaPath))
            File.WriteAllText(_metaPath, "[]");
    }

    public async Task<List<FormTemplate>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try { return await ReadAsync(); }
        finally { _lock.Release(); }
    }

    public async Task<FormTemplate?> GetByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync();
            return all.FirstOrDefault(t => t.Id == id);
        }
        finally { _lock.Release(); }
    }

    public async Task<string> AddAsync(
        string name, string fileName, byte[] bytes,
        List<FormField> fields, string uploadedBy)
    {
        await _lock.WaitAsync();
        try
        {
            var template = new FormTemplate
            {
                Name       = name,
                FileName   = fileName,
                UploadedBy = uploadedBy,
                Fields     = fields
            };

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            await File.WriteAllBytesAsync(
                Path.Combine(_bytesDir, $"{template.Id}{ext}"), bytes);

            var all = await ReadAsync();
            all.Add(template);
            await WriteAsync(all);
            return template.Id;
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var entry = all.FirstOrDefault(t => t.Id == id);
            if (entry is null) return;

            all.Remove(entry);
            await WriteAsync(all);

            foreach (var ext in new[] { ".docx", ".txt" })
            {
                var p = Path.Combine(_bytesDir, $"{id}{ext}");
                if (File.Exists(p)) File.Delete(p);
            }
        }
        finally { _lock.Release(); }
    }

    public byte[]? GetBytes(string id)
    {
        foreach (var ext in new[] { ".docx", ".txt" })
        {
            var p = Path.Combine(_bytesDir, $"{id}{ext}");
            if (File.Exists(p)) return File.ReadAllBytes(p);
        }
        return null;
    }

    public string? GetFileName(string id)
    {
        foreach (var ext in new[] { ".docx", ".txt" })
        {
            if (File.Exists(Path.Combine(_bytesDir, $"{id}{ext}")))
                return $"{id}{ext}";
        }
        return null;
    }

    private async Task<List<FormTemplate>> ReadAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(_metaPath);
            return JsonSerializer.Deserialize<List<FormTemplate>>(json, _opts) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read form_library.json");
            return [];
        }
    }

    private async Task WriteAsync(List<FormTemplate> data)
    {
        await File.WriteAllTextAsync(
            _metaPath, JsonSerializer.Serialize(data, _opts));
    }
}
