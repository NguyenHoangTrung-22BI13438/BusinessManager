using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize]
public class DocumentsModel : PageModel
{
    private readonly ElasticsearchChunkStore   _chunkStore;
    private readonly IngestionChannel   _channel;
    private readonly IngestionJobStore  _store;
    private readonly IngestionPipeline  _pipeline;
    private readonly ReparseChannel     _reparseChannel;
    private readonly ILogger<DocumentsModel> _log;
    private readonly UserContext        _userContext;
    private readonly PendingDocumentStore _pending;
    private readonly IWebHostEnvironment _env;

    public List<DocumentItem>           Documents        { get; private set; } = [];
    public List<PendingDocument>        PendingDocuments { get; private set; } = [];
    public Dictionary<string, long>     DeptCounts       { get; private set; } = [];
    public string CurrentSort    { get; private set; } = "name";
    public string CurrentDir     { get; private set; } = "asc";
    public string UserDepartment { get; private set; } = "";

    public DocumentsModel(
        ElasticsearchChunkStore chunkStore,
        IngestionChannel channel,
        IngestionJobStore store,
        UserContext userContext,
        IngestionPipeline pipeline,
        ReparseChannel reparseChannel,
        PendingDocumentStore pending,
        IWebHostEnvironment env,
        ILogger<DocumentsModel> log)
    {
        _chunkStore    = chunkStore;
        _channel        = channel;
        _store          = store;
        _userContext    = userContext;
        _pipeline       = pipeline;
        _reparseChannel = reparseChannel;
        _pending        = pending;
        _env            = env;
        _log            = log;
    }

    public async Task OnGetAsync(string? sort, string? dir)
    {
        CurrentSort = string.IsNullOrWhiteSpace(sort) ? "name" : sort;
        CurrentDir  = dir == "desc" ? "desc" : "asc";

        var isAdmin   = User.IsInRole("admin");
        var datasetId = await _userContext.EnsureDatasetAsync();

        // Admins see everything; regular users see only their dept's active docs
        DeptFilter? filter = isAdmin ? null : await _userContext.GetDeptFilterAsync();
        var chunks = await _chunkStore.GetByDatasetAsync(datasetId, filter);
        var docs = chunks
            .GroupBy(c => c.DocumentId)
            .Select(g => new DocumentItem(
                Id:         g.Key,
                Name:       g.First().DocumentName,
                Size:       0,
                Type:       "",
                RunStatus:  "DONE",
                ChunkCount: g.Count(),
                TokenCount: 0,
                Progress:   1.0,
                CreateTime: 0,
                Department: g.First().Department,
                DocType:    g.First().DocType,
                Scope:      g.First().Scope,
                Status:     g.First().Status))
            .ToList();
        Documents = SortDocuments(docs, CurrentSort, CurrentDir);

        if (isAdmin)
        {
            DeptCounts = await _chunkStore.GetDepartmentCountsAsync();
        }
        else
        {
            var rec = await _userContext.GetRecordAsync();
            UserDepartment   = rec.Department;
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

    private static readonly Dictionary<string, HashSet<string>> _allowedTagValues = new()
    {
        ["department"] = ["HR", "DEV", "TEST", "BA", "FINANCE"],
        ["docType"]    = ["Quy trình", "Quy định", "Quyết định", "Thông báo", "Biểu mẫu", "Tài liệu kỹ thuật"],
        ["scope"]      = ["Toàn công ty", "Nội bộ phòng ban", "Ban lãnh đạo"],
        ["status"]     = ["Đang hiệu lực", "Bản nháp", "Hết hiệu lực"],
    };

    public async Task<IActionResult> OnPostUpdateTagAsync(
        string documentId, string field, string value)
    {
        if (!User.IsInRole("admin")) return Forbid();
        if (!_allowedTagValues.TryGetValue(field, out var allowed) || !allowed.Contains(value))
            return BadRequest();
        await _chunkStore.UpdateDocumentFieldAsync(documentId, field, value);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostDeleteAsync(List<string> documentIds)
    {
        if (!User.IsInRole("admin")) return Forbid();
        if (documentIds is { Count: > 0 })
        {
            var cacheDir = Path.Combine(_env.WebRootPath, "doc-cache");
            foreach (var docId in documentIds)
            {
                try { await _chunkStore.DeleteByDocumentAsync(docId); } catch { }
                if (Directory.Exists(cacheDir))
                    foreach (var f in Directory.GetFiles(cacheDir, docId + ".*"))
                        try { System.IO.File.Delete(f); } catch { }
            }
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

    public async Task<IActionResult> OnPostUploadAsync(
        List<IFormFile> files,
        string? department, string? docType, string? scope, string? status)
    {
        if (files is null || files.Count == 0)
            return new JsonResult(new { jobs = Array.Empty<object>() });

        var dtype = (docType ?? "").Trim();
        var scp   = (scope   ?? "").Trim();
        var sts   = string.IsNullOrWhiteSpace(status) ? "Đang hiệu lực" : status.Trim();

        if (!User.IsInRole("admin"))
        {
            var rec   = await _userContext.GetRecordAsync();
            var dept  = rec.Department;
            var submitted = new List<object>();
            foreach (var f in files.Where(f => f.Length > 0))
            {
                await _pending.AddAsync(User.Identity!.Name!, f, dept, dtype);
                submitted.Add(new { fileName = f.FileName, pending = true });
            }
            return new JsonResult(new { jobs = Array.Empty<object>(), pending = submitted });
        }

        var adminDept = (department ?? "").Trim();

        var datasetId = await _userContext.EnsureDatasetAsync();
        var jobs = new List<object>();
        foreach (var f in files.Where(f => f.Length > 0))
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var jobId = Guid.NewGuid().ToString("N");
            var ct = f.ContentType ?? "application/octet-stream";
            _store.Add(new JobStatus { JobId = jobId, FileName = f.FileName });
            var job = new IngestionJob(jobId, datasetId, ms.ToArray(), f.FileName, ct,
                adminDept, dtype, scp, sts);
            if (!_channel.Writer.TryWrite(job)) await _channel.Writer.WriteAsync(job);
            jobs.Add(new { jobId, fileName = f.FileName });
        }
        return new JsonResult(new { jobs });
    }
}
