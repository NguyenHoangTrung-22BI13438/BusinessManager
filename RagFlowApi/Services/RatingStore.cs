using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class RatingStore
{
    private readonly string                       _filePath;
    private readonly ILogger<RatingStore>         _log;
    private readonly SemaphoreSlim                _lock = new(1, 1);
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public RatingStore(IWebHostEnvironment env, ILogger<RatingStore> log)
    {
        _filePath = Path.Combine(env.ContentRootPath, "ratings.json");
        _log      = log;
        if (!File.Exists(_filePath))
            File.WriteAllText(_filePath, "[]");
    }

    public async Task AddOrUpdateAsync(RatingEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAllInternalAsync();
            var idx = all.FindIndex(r =>
                r.SessionId == entry.SessionId &&
                r.MessageId == entry.MessageId &&
                r.Username  == entry.Username);
            if (idx >= 0) all[idx] = entry;
            else          all.Add(entry);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, _writeOptions));
        }
        catch (Exception ex) { _log.LogError(ex, "Failed to write ratings.json"); }
        finally { _lock.Release(); }
    }

    public async Task<Dictionary<string, bool>> GetRatingsForUserInSessionAsync(
        string username, string sessionId)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAllInternalAsync();
            return all
                .Where(r => r.Username == username && r.SessionId == sessionId)
                .ToDictionary(r => r.MessageId, r => r.Positive);
        }
        finally { _lock.Release(); }
    }

    public async Task<List<RatingEntry>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try   { return await ReadAllInternalAsync(); }
        finally { _lock.Release(); }
    }

    private async Task<List<RatingEntry>> ReadAllInternalAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<List<RatingEntry>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read ratings.json");
            return [];
        }
    }
}
