using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class UserContext
{
    private readonly IHttpContextAccessor _http;
    private readonly UserStore            _store;
    private readonly ILogger<UserContext> _log;

    private UserRecord? _record;
    private string?     _cachedDatasetId;

    public UserContext(
        IHttpContextAccessor http,
        UserStore store,
        ILogger<UserContext> log)
    {
        _http  = http;
        _store = store;
        _log   = log;
    }

    public string Username =>
        _http.HttpContext?.User?.Identity?.Name
        ?? throw new InvalidOperationException(
            "UserContext accessed outside an authenticated request.");

    public async Task<UserRecord> GetRecordAsync()
    {
        if (_record is not null) return _record;
        _record = await _store.GetByUsernameAsync(Username)
                  ?? throw new InvalidOperationException(
                      $"No user record found for '{Username}'.");
        return _record;
    }

    // Admins: null (no filter). Users: [Department, "General"]. No dept: ["General"].
    public async Task<IReadOnlyList<string>?> GetAllowedCategoriesAsync()
    {
        var record = await GetRecordAsync();
        if (record.IsAdmin) return null;
        var dept = (record.Department ?? "").Trim();
        return string.IsNullOrEmpty(dept)
            ? (IReadOnlyList<string>)["General"]
            : [dept, "General"];
    }

    // Returns the current user's dataset ID, creating a local UUID on first use.
    public async Task<string> EnsureDatasetAsync()
    {
        if (_cachedDatasetId is not null) return _cachedDatasetId;

        var record = await GetRecordAsync();

        if (!string.IsNullOrWhiteSpace(record.DatasetId))
        {
            _cachedDatasetId = record.DatasetId;
            return _cachedDatasetId;
        }

        _log.LogInformation("Assigning local dataset ID for user '{User}'.", Username);
        var datasetId = Guid.NewGuid().ToString("N");
        record.DatasetId = datasetId;
        await _store.UpdateAsync(record);

        _cachedDatasetId = datasetId;
        return _cachedDatasetId;
    }

    // Admins query their own dataset; non-admins query the admin's dataset.
    public async Task<string> GetSharedDatasetIdAsync()
    {
        var record = await GetRecordAsync();

        if (record.IsAdmin)
            return await EnsureDatasetAsync();

        var admin = await _store.GetAdminWithDatasetAsync()
            ?? throw new InvalidOperationException(
                "No admin dataset found. An admin must upload at least one document first.");

        return admin.DatasetId;
    }
}
