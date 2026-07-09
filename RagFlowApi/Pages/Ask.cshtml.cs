using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authorization;
using RagFlowApi.Models;
using RagFlowApi.Services;
using System.Text.Json;

namespace RagFlowApi.Pages;

[Authorize]
public class AskModel : PageModel
{
    private readonly RagFlowService    _svc;
    private readonly ConversationStore _conversations;
    private readonly IngestionChannel  _channel;
    private readonly IngestionJobStore _store;
    private readonly UserContext       _userContext;
    private readonly IMemoryCache      _cache;
    private readonly RatingStore       _ratings;

    public List<SessionItem>        Sessions        { get; private set; } = [];
    public string?                  SessionId       { get; private set; }
    public string?                  SessionName     { get; private set; }
    public string?                  ErrorMessage    { get; private set; }
    public string?                  SuccessMessage  { get; private set; }
    public List<ChatMessage>        Messages        { get; private set; } = [];
    public string                   PendingJobsJson { get; private set; } = "[]";
    public Dictionary<string, bool> Ratings         { get; private set; } = [];

    public AskModel(RagFlowService svc, ConversationStore conversations,
                    IngestionChannel channel, IngestionJobStore store,
                    UserContext userContext, IMemoryCache cache, RatingStore ratings)
    {
        _svc = svc; _conversations = conversations;
        _channel = channel; _store = store;
        _userContext = userContext; _cache = cache; _ratings = ratings;
    }

    public async Task OnGetAsync(string? sessionId = null)
    {
        var username = User.Identity?.Name ?? "";
        Sessions = await _conversations.ListByUsernameAsync(username);
        SessionId = sessionId;
        SessionName = Sessions.FirstOrDefault(s => s.Id == sessionId)?.Name;
        if (sessionId != null)
        {
            Messages = CollapseRegenerations(await _conversations.LoadAsync(sessionId) ?? []);
            ApplyCachedChunks(sessionId);
            await LoadRatingsAsync(sessionId);
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(string? sessionName)
    {
        var username = User.Identity?.Name ?? "";
        var name = string.IsNullOrWhiteSpace(sessionName)
            ? $"Chat {DateTime.Now:MMM d, HH:mm}" : sessionName;
        var id = await _conversations.CreateSessionAsync(username, name);
        return RedirectToPage(new { sessionId = id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string sessionId)
    {
        await _conversations.DeleteSessionAsync(sessionId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(string sessionId, string newName)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(newName))
            return new JsonResult(new { ok = false, error = "Invalid input." })
                   { StatusCode = 400 };

        newName = newName.Trim();
        if (newName.Length > 80) newName = newName[..80];

        try
        {
            await _conversations.RenameSessionAsync(sessionId, newName);
            return new JsonResult(new { ok = true, name = newName });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message })
                   { StatusCode = 500 };
        }
    }

    public async Task OnPostAskAsync(string sessionId, string? question, List<IFormFile>? files)
    {
        var assistantId = await _userContext.EnsureAssistantAsync();
        var username = User.Identity?.Name ?? "";
        Sessions = await _conversations.ListByUsernameAsync(username);
        SessionId = sessionId;
        SessionName = Sessions.FirstOrDefault(s => s.Id == sessionId)?.Name;

        var (count, ok, err, jobs) = await EnqueueFilesAsync(files);
        SuccessMessage = ok;
        ErrorMessage = err;
        PendingJobsJson = JsonSerializer.Serialize(jobs);

        var hasQ = !string.IsNullOrWhiteSpace(question);
        if (!hasQ && count == 0)
        {
            ErrorMessage = "Please enter a question or attach at least one file.";
            Messages = await _conversations.LoadAsync(sessionId) ?? [];
            return;
        }

        if (hasQ)
        {
            var allowedCategories = await _userContext.GetAllowedCategoriesAsync();
            string completionJson = "";
            try { completionJson = await _svc.AskQuestionAsync(assistantId, sessionId, question!, allowedCategories); }
            catch (Exception ex) { ErrorMessage = $"Something went wrong: {ex.Message}"; }

            Messages = await _conversations.LoadAsync(sessionId) ?? [];

            if (!string.IsNullOrEmpty(completionJson) && Messages.Count > 0)
            {
                var last = Messages[^1];
                if (last.Role != "user" && last.Chunks.Count == 0)
                {
                    var chunks = RagFlowService.ParseCompletionChunks(completionJson);
                    if (chunks.Count > 0)
                    {
                        Messages[^1] = last with { Chunks = chunks };
                        _cache.Set($"chunks:{sessionId}", chunks, TimeSpan.FromHours(24));
                    }
                }
            }
        }
        else
        {
            Messages = await _conversations.LoadAsync(sessionId) ?? [];
        }

        ApplyCachedChunks(sessionId);
        await LoadRatingsAsync(sessionId);
    }

    public async Task OnPostRegenerateAsync(string sessionId)
    {
        var assistantId = await _userContext.EnsureAssistantAsync();
        var username = User.Identity?.Name ?? "";
        Sessions = await _conversations.ListByUsernameAsync(username);
        SessionId = sessionId;
        SessionName = Sessions.FirstOrDefault(s => s.Id == sessionId)?.Name;

        var history = await _conversations.LoadAsync(sessionId) ?? [];
        var lastUser = history.LastOrDefault(m => m.Role == "user");

        if (lastUser is null)
        {
            ErrorMessage = "No previous question found to regenerate.";
            Messages = history;
            return;
        }

        var allowedCategories = await _userContext.GetAllowedCategoriesAsync();
        string completionJson = "";
        try { completionJson = await _svc.AskQuestionAsync(assistantId, sessionId, lastUser.Content, allowedCategories); }
        catch (Exception ex) { ErrorMessage = $"Regeneration failed: {ex.Message}"; }

        Messages = CollapseRegenerations(await _conversations.LoadAsync(sessionId) ?? []);

        if (!string.IsNullOrEmpty(completionJson) && Messages.Count > 0)
        {
            var last = Messages[^1];
            if (last.Role != "user" && last.Chunks.Count == 0)
            {
                var chunks = RagFlowService.ParseCompletionChunks(completionJson);
                if (chunks.Count > 0)
                {
                    Messages[^1] = last with { Chunks = chunks };
                    _cache.Set($"chunks:{sessionId}", chunks, TimeSpan.FromHours(24));
                }
            }
        }

        ApplyCachedChunks(sessionId);
        await LoadRatingsAsync(sessionId);
    }

    public async Task<IActionResult> OnPostRateAsync(string sessionId, string messageId, bool positive)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(messageId))
            return new JsonResult(new { ok = false }) { StatusCode = 400 };

        var username = User.Identity?.Name ?? "";
        await _ratings.AddOrUpdateAsync(new RatingEntry(
            sessionId, messageId, username, positive, DateTime.UtcNow));
        return new JsonResult(new { ok = true });
    }

    private async Task LoadRatingsAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var username = User.Identity?.Name ?? "";
        Ratings = await _ratings.GetRatingsForUserInSessionAsync(username, sessionId);
    }

    private void ApplyCachedChunks(string sessionId)
    {
        if (Messages.Count == 0) return;
        var last = Messages[^1];
        if (last.Role != "user" && last.Chunks.Count == 0
            && _cache.TryGetValue($"chunks:{sessionId}", out List<RagChunk>? cached)
            && cached?.Count > 0)
            Messages[^1] = last with { Chunks = cached };
    }

    private static List<ChatMessage> CollapseRegenerations(List<ChatMessage> msgs)
    {
        var result = new List<ChatMessage>();
        for (int i = 0; i < msgs.Count; i++)
        {
            var m = msgs[i];
            if (m.Role == "user")
            {
                bool sup = i + 2 < msgs.Count
                    && msgs[i + 2].Role == "user"
                    && msgs[i + 2].Content == m.Content;
                if (sup) i++; else result.Add(m);
            }
            else result.Add(m);
        }
        return result;
    }

    private async Task<(int Count, string? Ok, string? Err, List<object> Jobs)>
        EnqueueFilesAsync(List<IFormFile>? files)
    {
        if (files == null) return (0, null, null, []);
        var valid = files.Where(f => f.Length > 0).ToList();
        if (valid.Count == 0) return (0, null, null, []);

        if (!User.IsInRole("admin"))
            return (0, null, "Only admins can attach documents to the knowledge base.", []);

        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        int enqueued = 0;
        var failed = new List<string>();
        var jobs = new List<object>();

        foreach (var file in valid)
        {
            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var jobId = Guid.NewGuid().ToString("N");
                var ct = file.ContentType ?? "application/octet-stream";

                _store.Add(new JobStatus { JobId = jobId, FileName = file.FileName });
                var job = new IngestionJob(jobId, datasetId, ms.ToArray(), file.FileName, ct);
                if (!_channel.Writer.TryWrite(job)) await _channel.Writer.WriteAsync(job);

                jobs.Add(new { jobId, fileName = file.FileName });
                enqueued++;
            }
            catch (Exception ex) { failed.Add($"{file.FileName} ({ex.Message})"); }
        }

        string? ok = enqueued > 0 ? $"Queued {enqueued} file{(enqueued > 1 ? "s" : "")} for processing." : null;
        string? err = failed.Count > 0 ? $"Could not queue: {string.Join("; ", failed)}" : null;
        return (enqueued, ok, err, jobs);
    }
}
