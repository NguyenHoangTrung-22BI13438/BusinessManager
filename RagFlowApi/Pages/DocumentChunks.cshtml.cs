using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize]
public class DocumentChunksModel : PageModel
{
    private readonly RagFlowService _svc;
    private readonly UserContext    _userContext;
    private readonly IWebHostEnvironment _env;

    public string DocumentId   { get; private set; } = "";
    public string DocumentName { get; private set; } = "";
    public List<DocumentChunk> Chunks { get; private set; } = [];
    public string? Search { get; private set; }

    public bool IsImageDocument =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }
        .Any(ext => DocumentName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    // True for actual PDFs, and for .docx documents that have a converted
    // preview PDF cached (see IngestionPipeline.CacheDocxPreviewAsync).
    public bool IsPdfDocument =>
        DocumentName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        (DocumentName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
         System.IO.File.Exists(Path.Combine(_env.WebRootPath, "doc-cache", DocumentId + ".pdf")));

    public DocumentChunksModel(RagFlowService svc, UserContext userContext, IWebHostEnvironment env)
    {
        _svc         = svc;
        _userContext  = userContext;
        _env         = env;
    }

    [BindProperty] public string? ChunkId { get; set; }
    [BindProperty] public string? NewContent { get; set; }
    [BindProperty] public string? DocId { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(ChunkId) ||
            string.IsNullOrWhiteSpace(NewContent) ||
            string.IsNullOrWhiteSpace(DocId))
            return BadRequest();

        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        await _svc.UpdateChunkAsync(datasetId, DocId, ChunkId, NewContent);
        return RedirectToPage(new { id = DocId });
    }

    public async Task<IActionResult> OnGetAsync(string? id, string? search)
    {
        if (string.IsNullOrWhiteSpace(id)) return RedirectToPage("Documents");

        DocumentId = id;
        Search     = search;

        // Resolve the current user's dataset — never the global appsettings value.
        var datasetId = await _userContext.GetSharedDatasetIdAsync();

        var docs = await _svc.ListDocumentsAsync(datasetId);
        DocumentName = docs.FirstOrDefault(d => d.Id == id)?.Name ?? "Document";

        Chunks = await _svc.ListChunksAsync(datasetId, id);

        if (!string.IsNullOrWhiteSpace(search))
            Chunks = Chunks
                .Where(c => c.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return Page();
    }
}
