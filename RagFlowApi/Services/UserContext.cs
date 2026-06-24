using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Scoped service (one instance per HTTP request) that resolves the
/// currently logged-in user's RAGFlow dataset ID and assistant ID.
///
/// Both IDs are created lazily on first use:
///   - DatasetId  is created the first time the user uploads a file.
///   - AssistantId is created the first time the user asks a question.
///
/// Once created, each ID is saved back to users.json via UserStore
/// so that subsequent requests skip creation entirely.
///
/// Results are cached for the lifetime of the current request so that
/// multiple calls within one request do not hit the file or RAGFlow twice.
/// </summary>
public class UserContext
{
    private readonly IHttpContextAccessor   _http;
    private readonly UserStore              _store;
    private readonly RagFlowService         _svc;
    private readonly ILogger<UserContext>   _log;

    // ── Per-request cache ─────────────────────────────────────────────────────
    private UserRecord? _record;
    private string?     _cachedDatasetId;
    private string?     _cachedAssistantId;

    public UserContext(
        IHttpContextAccessor http,
        UserStore store,
        RagFlowService svc,
        ILogger<UserContext> log)
    {
        _http  = http;
        _store = store;
        _svc   = svc;
        _log   = log;
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the username of the currently authenticated user.
    /// Throws InvalidOperationException if called outside an authenticated request.
    /// </summary>
    public string Username =>
        _http.HttpContext?.User?.Identity?.Name
        ?? throw new InvalidOperationException(
            "UserContext accessed outside an authenticated request.");

    // ── Record loader ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the current user's record from users.json.
    /// Result is cached for the lifetime of this request.
    /// </summary>
    private async Task<UserRecord> GetRecordAsync()
    {
        if (_record is not null) return _record;

        _record = await _store.GetByUsernameAsync(Username)
                  ?? throw new InvalidOperationException(
                      $"No user record found for '{Username}'.");

        return _record;
    }

    // ── Dataset ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current user's RAGFlow dataset ID.
    /// If no dataset exists yet, creates one in RAGFlow, saves the ID to
    /// users.json, and returns it. Subsequent calls return the cached value.
    /// </summary>
    /// <returns>A non-empty RAGFlow dataset ID.</returns>
    public async Task<string> EnsureDatasetAsync()
    {
        // Return cached value for this request
        if (_cachedDatasetId is not null) return _cachedDatasetId;

        var record = await GetRecordAsync();

        // Dataset already created in a previous request
        if (!string.IsNullOrWhiteSpace(record.DatasetId))
        {
            _cachedDatasetId = record.DatasetId;
            return _cachedDatasetId;
        }

        // ── First upload — create the dataset now ─────────────────────────────
        _log.LogInformation(
            "Creating dataset for user '{User}' on first upload.", Username);

        var datasetId = await _svc.CreateDatasetAsync(Username);

        // Save back to users.json so the next request finds it
        record.DatasetId = datasetId;
        await _store.UpdateAsync(record);

        _cachedDatasetId = datasetId;
        return _cachedDatasetId;
    }
    /// <summary>
    /// Returns the dataset ID that the current user should query.
    /// Admins return their own dataset (the shared corpus).
    /// Normal users return the admin's dataset — they never get one of their own.
    /// </summary>
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

    // ── Assistant ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current user's RAGFlow assistant ID.
    /// If no assistant exists yet, ensures the dataset exists first
    /// (calling <see cref="EnsureDatasetAsync"/> internally), then creates
    /// the assistant, saves the ID to users.json, and returns it.
    /// Subsequent calls return the cached value.
    /// </summary>
    /// <returns>A non-empty RAGFlow assistant ID.</returns>
    public async Task<string> EnsureAssistantAsync()
    {
        if (_cachedAssistantId is not null) return _cachedAssistantId;

        var record = await GetRecordAsync();

        if (!string.IsNullOrWhiteSpace(record.AssistantId))
        {
            _cachedAssistantId = record.AssistantId;

            // Ensure the dataset is bound — covers manually-created assistants
            // and accounts created before auto-binding was added.
            if (!record.DatasetBound)
            {
                var dsId = await GetSharedDatasetIdAsync();
                try
                {
                    await _svc.BindDatasetToAssistantAsync(_cachedAssistantId, dsId);
                    record.DatasetBound = true;
                    await _store.UpdateAsync(record);
                    _log.LogInformation(
                        "Bound dataset {DS} to existing assistant {AS} for user '{User}'.",
                        dsId, _cachedAssistantId, Username);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Dataset binding failed for '{User}' — will retry next request.", Username);
                }
            }

            return _cachedAssistantId;
        }

        // was:  var datasetId = await EnsureDatasetAsync();
        var datasetId = await GetSharedDatasetIdAsync();

        _log.LogInformation(
            "Creating assistant for user '{User}' on first question.", Username);

        var assistantId = await _svc.CreateAssistantAsync(Username);

        // Bind the dataset now that the assistant exists
        // Only works if the dataset has at least one parsed file.
        // If binding fails, we still save the assistantId — binding can be retried.
        try
        {
            await _svc.BindDatasetToAssistantAsync(assistantId, datasetId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Dataset binding failed for user '{User}' — " +
                "assistant created but dataset not yet bound. " +
                "Will retry on next question.", Username);
        }

        record.AssistantId = assistantId;
        await _store.UpdateAsync(record);

        _cachedAssistantId = assistantId;
        return _cachedAssistantId;
    }
}
