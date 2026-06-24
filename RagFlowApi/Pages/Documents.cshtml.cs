using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;
using UglyToad.PdfPig.Logging;

namespace RagFlowApi.Pages;

[Authorize]
public class DocumentsModel : PageModel
{
    private readonly RagFlowService    _svc;
    private readonly IngestionChannel  _channel;
    private readonly IngestionJobStore _store;
    private readonly IngestionPipeline _pipeline;
    private readonly ReparseChannel    _reparseChannel;
    private readonly ILogger<DocumentsModel> _log;
    private readonly UserContext _userContext;
    private readonly PendingDocumentStore _pending;

    public List<DocumentItem> Documents { get; private set; } = [];
    public List<PendingDocument> PendingDocuments { get; private set; } = []; // add
    public string CurrentSort { get; private set; } = "name";
    public string CurrentDir  { get; private set; } = "asc";

    public DocumentsModel(
    RagFlowService svc,
    IngestionChannel channel,
    IngestionJobStore store,
    UserContext userContext,
    IngestionPipeline pipeline,
    ReparseChannel reparseChannel,
    PendingDocumentStore pending,                 // ← add this
    ILogger<DocumentsModel> log)
    {
        _svc = svc;
        _channel = channel;
        _store = store;
        _userContext = userContext;
        _pipeline = pipeline;
        _reparseChannel = reparseChannel;
        _pending = pending;                       // ← add this
        _log = log;
    }

    public async Task OnGetAsync(string? sort, string? dir)
    {
        CurrentSort = string.IsNullOrWhiteSpace(sort) ? "name" : sort;
        CurrentDir  = dir == "desc" ? "desc" : "asc";

        if (User.IsInRole("admin"))
        {
            var datasetId = await _userContext.EnsureDatasetAsync();
            Documents = SortDocuments(
                await _svc.ListDocumentsAsync(datasetId), CurrentSort, CurrentDir);
        }
        else
        {
            PendingDocuments = await _pending.GetByUserAsync(User.Identity!.Name!);
        }
    }

    private static List<DocumentItem> SortDocuments(
        List<DocumentItem> docs, string sort, string dir)
    {
        IOrderedEnumerable<DocumentItem> sorted = sort switch
        {
            "size"     => docs.OrderBy(d => d.Size),
            "status"   => docs.OrderBy(d => d.RunStatus, StringComparer.OrdinalIgnoreCase),
            "chunks"   => docs.OrderBy(d => d.ChunkCount),
            "uploaded" => docs.OrderBy(d => d.CreateTime),
            _          => docs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        };
        return [.. dir == "desc" ? sorted.Reverse() : sorted];
    }

    public async Task<IActionResult> OnPostDeleteAsync(List<string> documentIds)
    {
        if (!User.IsInRole("admin")) return Forbid();
        if (documentIds is { Count: > 0 })
        {
            var datasetId = await _userContext.EnsureDatasetAsync();
            try { await _svc.DeleteDocumentsAsync(datasetId, [.. documentIds]); }
            catch { }
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReparseAsync(string documentId, string fileName)
    {
        if (!User.IsInRole("admin")) return Forbid();
        var datasetId = await _userContext.EnsureDatasetAsync();
        try { await _pipeline.ReingestAsync(datasetId, documentId, fileName); }
        catch (Exception ex) { _log.LogError(ex, "Reparse failed for {Doc}", documentId); }
        return RedirectToPage();
    }

    // Queues each selected document onto the single-consumer reparse worker
    // instead of reparsing inline — keeps the OCR backend processing one
    // document at a time and lets the client poll progress per file, the
    // same way /Documents?handler=Upload already does.
    // Returns JSON { "jobs": [{ "jobId": "...", "fileName": "..." }] }
    public async Task<IActionResult> OnPostBatchReparseAsync(
        List<string> documentIds, List<string> fileNames)
    {
        if (!User.IsInRole("admin")) return Forbid();
        if (documentIds is null || documentIds.Count == 0)
            return new JsonResult(new { jobs = Array.Empty<object>() });

        var datasetId = await _userContext.EnsureDatasetAsync();
        var jobs = new List<object>();

        for (int i = 0; i < documentIds.Count; i++)
        {
            var docId    = documentIds[i];
            var fileName = i < (fileNames?.Count ?? 0) ? fileNames![i] : docId;
            var jobId    = Guid.NewGuid().ToString("N");

            _store.Add(new JobStatus { JobId = jobId, FileName = fileName });
            var job = new ReparseJob(jobId, datasetId, docId, fileName);
            if (!_reparseChannel.Writer.TryWrite(job))
                await _reparseChannel.Writer.WriteAsync(job);

            jobs.Add(new { jobId, fileName });
        }
        return new JsonResult(new { jobs });
    }

    // Returns JSON { "jobs": [{ "jobId": "...", "fileName": "..." }] }
    // so the client can start polling without waiting for OCR.
    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files)
    {
        if (files is null || files.Count == 0)
            return new JsonResult(new { jobs = Array.Empty<object>() });

        // ── Non-admin: route to pending store ────────────────────────────────────
        if (!User.IsInRole("admin"))
        {
            var submitted = new List<object>();
            foreach (var f in files.Where(f => f.Length > 0))
            {
                await _pending.AddAsync(User.Identity!.Name!, f);
                submitted.Add(new { fileName = f.FileName, pending = true });
            }
            return new JsonResult(new { jobs = Array.Empty<object>(), pending = submitted });
        }

        // ── Admin: straight into the ingestion queue ──────────────────────────────
        var datasetId = await _userContext.EnsureDatasetAsync();
        var jobs = new List<object>();
        foreach (var f in files.Where(f => f.Length > 0))
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var jobId = Guid.NewGuid().ToString("N");
            var ct = f.ContentType ?? "application/octet-stream";
            _store.Add(new JobStatus { JobId = jobId, FileName = f.FileName });
            var job = new IngestionJob(jobId, datasetId, ms.ToArray(), f.FileName, ct);
            if (!_channel.Writer.TryWrite(job)) await _channel.Writer.WriteAsync(job);
            jobs.Add(new { jobId, fileName = f.FileName });
        }
        return new JsonResult(new { jobs });
    }
}