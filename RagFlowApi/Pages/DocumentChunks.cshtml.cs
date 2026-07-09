using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;

namespace RagFlowApi.Pages;

[Authorize]
public class DocumentChunksModel : PageModel
{
    private readonly VectorChunkStore       _vectorStore;
    private readonly OllamaEmbeddingClient  _embedder;
    private readonly UserContext            _userContext;
    private readonly IWebHostEnvironment    _env;

    public string DocumentId   { get; private set; } = "";
    public string DocumentName { get; private set; } = "";
    public List<DocumentChunk> Chunks { get; private set; } = [];
    public string? Search { get; private set; }

    public bool IsImageDocument =>
        new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }
        .Any(ext => DocumentName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public bool IsPdfDocument =>
        DocumentName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        (DocumentName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) &&
         System.IO.File.Exists(Path.Combine(_env.WebRootPath, "doc-cache", DocumentId + ".pdf")));

    public DocumentChunksModel(
        VectorChunkStore vectorStore,
        OllamaEmbeddingClient embedder,
        UserContext userContext,
        IWebHostEnvironment env)
    {
        _vectorStore = vectorStore;
        _embedder    = embedder;
        _userContext  = userContext;
        _env         = env;
    }

    [BindProperty] public string? ChunkId     { get; set; }
    [BindProperty] public string? NewContent  { get; set; }
    [BindProperty] public string? DocId       { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(ChunkId) ||
            string.IsNullOrWhiteSpace(NewContent) ||
            string.IsNullOrWhiteSpace(DocId))
            return BadRequest();

        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        var all = await _vectorStore.GetByDatasetAsync(datasetId);
        var existing = all.FirstOrDefault(c => c.Id == ChunkId && c.DocumentId == DocId);
        if (existing is null) return BadRequest();

        var embedding = await _embedder.EmbedAsync(NewContent);
        var updated = existing with { Content = NewContent, Embedding = embedding };
        await _vectorStore.AddRangeAsync([updated]);

        return RedirectToPage(new { id = DocId });
    }

    public async Task<IActionResult> OnGetAsync(string? id, string? search)
    {
        if (string.IsNullOrWhiteSpace(id)) return RedirectToPage("Documents");

        DocumentId = id;
        Search     = search;

        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        var all = await _vectorStore.GetByDatasetAsync(datasetId);
        var docChunks = all.Where(c => c.DocumentId == id).ToList();

        DocumentName = docChunks.FirstOrDefault()?.DocumentName ?? "Document";

        Chunks = docChunks
            .OrderBy(c => c.Keywords
                .FirstOrDefault(k => k.StartsWith("seq:"))
                ?.Substring(4) ?? "9999")
            .Select(c => new DocumentChunk(
                Id:         c.Id,
                Content:    c.Content,
                DocumentId: c.DocumentId,
                Available:  true,
                ImageId:    null,
                Keywords:   c.Keywords))
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
            Chunks = Chunks
                .Where(c => c.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return Page();
    }
}
