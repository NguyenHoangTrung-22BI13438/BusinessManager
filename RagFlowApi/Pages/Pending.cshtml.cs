using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize(Roles = "admin")]
public class PendingModel : PageModel
{
    private readonly PendingDocumentStore _pending;
    private readonly IngestionChannel _channel;
    private readonly IngestionJobStore _jobs;
    private readonly UserContext _userContext;

    public List<PendingDocument> Documents { get; private set; } = [];

    public PendingModel(
        PendingDocumentStore pending,
        IngestionChannel channel,
        IngestionJobStore jobs,
        UserContext userContext)
    {
        _pending = pending;
        _channel = channel;
        _jobs = jobs;
        _userContext = userContext;
    }

    public async Task OnGetAsync()
        => Documents = await _pending.GetPendingAsync();

    public async Task<IActionResult> OnPostApproveAsync(string id)
    {
        var doc = await _pending.GetByIdAsync(id);
        if (doc is null) return RedirectToPage();

        var bytes = await System.IO.File.ReadAllBytesAsync(doc.FilePath);
        var datasetId = await _userContext.EnsureDatasetAsync(); // admin's dataset
        var jobId = Guid.NewGuid().ToString("N");

        _jobs.Add(new JobStatus { JobId = jobId, FileName = doc.FileName });
        var job = new IngestionJob(
            jobId, datasetId, bytes, doc.FileName, doc.ContentType);

        if (!_channel.Writer.TryWrite(job)) await _channel.Writer.WriteAsync(job);

        await _pending.UpdateStatusAsync(id, PendingStatus.Approved);
        _pending.DeleteFile(doc);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(string id)
    {
        var doc = await _pending.GetByIdAsync(id);
        if (doc is not null)
        {
            _pending.DeleteFile(doc);
            await _pending.UpdateStatusAsync(id, PendingStatus.Rejected);
        }
        return RedirectToPage();
    }
}