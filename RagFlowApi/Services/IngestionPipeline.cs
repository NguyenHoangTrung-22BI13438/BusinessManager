using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class IngestionPipeline
{
    private readonly IParser _ocr;
    private readonly DotsOcrClient _ocrClient;
    private readonly LayoutChunker _chunker;
    private readonly OllamaEmbeddingClient _embedder;
    private readonly VectorChunkStore _vectorStore;
    private readonly ILogger<IngestionPipeline> _log;
    private readonly IWebHostEnvironment _env;

    public IngestionPipeline(
        IParser ocr, DotsOcrClient ocrClient,
        LayoutChunker chunker,
        OllamaEmbeddingClient embedder, VectorChunkStore vectorStore,
        ILogger<IngestionPipeline> log, IWebHostEnvironment env)
    {
        _ocr = ocr;
        _ocrClient = ocrClient;
        _chunker = chunker;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _log = log;
        _env = env;
    }

    // ── docx → PDF conversion (visual preview only, text extraction is unaffected) ──
    // Requires LibreOffice installed. Resolves the soffice.exe path directly
    // instead of relying on PATH, since winget installs don't always register it.
    // Used so the chunk viewer can show a rendered page image for .docx the
    // same way it does for PDFs.
    private static readonly string[] SofficeCandidates =
    [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "soffice" // fall back to PATH if available
    ];

    private async Task<byte[]?> ConvertDocxToPdfAsync(byte[] docxBytes, string fileName)
    {
        var sofficePath = SofficeCandidates.FirstOrDefault(File.Exists) ?? "soffice";

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var docxPath = Path.Combine(tempDir, fileName);

        try
        {
            await File.WriteAllBytesAsync(docxPath, docxBytes);

            // Each job gets its own isolated LO profile so that a running
            // LibreOffice GUI instance does not intercept the headless conversion
            // (without this, LO silently exits 0 and produces no PDF).
            var loProfile = "file:///" + tempDir.Replace('\\', '/') + "/lo-profile";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sofficePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            // Use ArgumentList (not Arguments) so .NET handles per-token quoting —
            // string-style Arguments fails when file paths contain spaces.
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add($"-env:UserInstallation={loProfile}");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add("pdf");
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(tempDir);
            psi.ArgumentList.Add(docxPath);

            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync();

            var pdfPath = Path.ChangeExtension(docxPath, ".pdf");
            if (!File.Exists(pdfPath))
            {
                _log.LogWarning("docx→pdf conversion failed for {File}", fileName);
                return null;
            }

            return await File.ReadAllBytesAsync(pdfPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "docx→pdf conversion threw for {File}", fileName);
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Converts and caches a docx preview PDF alongside the original file cache.
    // Failure is non-fatal — the chunk viewer just falls back to no preview.
    private async Task CacheDocxPreviewAsync(byte[] docxBytes, string fileName, string documentId)
    {
        var pdfBytes = await ConvertDocxToPdfAsync(docxBytes, fileName);
        if (pdfBytes is null) return;

        var pdfCachePath = Path.Combine(_env.WebRootPath, "doc-cache", documentId + ".pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdfCachePath)!);
        await File.WriteAllBytesAsync(pdfCachePath, pdfBytes);
    }

    // ── Smart PDF text extraction ─────────────────────────────────────────────
    // Uses PdfPig to pull selectable text from each page.
    // Returns a non-empty list when the PDF has extractable text (i.e. it is
    // not a pure scan).  Returns an empty list on failure or if the PDF has
    // no text layer, which tells the caller to fall back to vision OCR.
    // Returns (textElements, emptyPageNumbers).
    // emptyPageNumbers contains pages PdfPig found no text on — these need vision OCR.
    // Returns ([], []) on PdfPig failure — caller should fall back to full OCR.
    private static (List<LayoutElement> Text, List<int> EmptyPages)
        TryExtractPdfText(byte[] pdfBytes)
    {
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var textElements = new List<LayoutElement>();
            var emptyPages = new List<int>();
            int page = 0;

            foreach (var p in pdf.GetPages())
            {
                page++;
                var text = p.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    textElements.Add(new LayoutElement
                    {
                        Category = LayoutCategory.Text,
                        Text = text,
                        Page = page,
                        Bbox = new BBox(0, 0, 1000, 1000)
                    });
                else
                    emptyPages.Add(page);
            }
            return (textElements, emptyPages);
        }
        catch { return ([], []); }
    }

    // ── IngestAsync (new uploads via IFormFile) ───────────────────────────────
    public async Task<IngestionResult> IngestAsync(string datasetId, IFormFile file, string category = "General")
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var ct = file.ContentType ?? "application/octet-stream";

        _log.LogInformation("Ingesting {File} ({Size} bytes) → dataset {DS}",
            file.FileName, bytes.Length, datasetId);

        List<LayoutElement> elements;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (ext == ".pdf")
        {
            var (textEls, emptyPages) = TryExtractPdfText(bytes);

            if (emptyPages.Count == 0 && textEls.Count > 0)
            {
                // Fully digital PDF — no vision OCR needed
                _log.LogInformation("PDF has selectable text on all pages — skipping vision OCR.");
                elements = textEls;
            }
            else if (textEls.Count == 0)
            {
                // Pure scan or PdfPig failed entirely — full vision OCR
                _log.LogInformation("PDF has no selectable text — routing through vision OCR.");
                elements = await _ocrClient.ExtractLayoutFromPdfAsync(bytes);
            }
            else
            {
                // Mixed: some pages digital, some scanned
                _log.LogInformation(
                    "PDF is mixed ({T} text pages, {S} scanned pages) — merging.",
                    textEls.Count, emptyPages.Count);
                var ocrAll = await _ocrClient.ExtractLayoutFromPdfAsync(bytes);
                var ocrByPage = ocrAll.GroupBy(e => e.Page)
                                      .ToDictionary(g => g.Key, g => g.ToList());
                elements = [.. textEls];
                foreach (var pg in emptyPages)
                    if (ocrByPage.TryGetValue(pg, out var pgEls))
                        elements.AddRange(pgEls);
                elements = [.. elements.OrderBy(e => e.Page)];
            }
        }
        else if (ct.StartsWith("image/"))
        {
            elements = await _ocrClient.ExtractLayoutAsync(bytes, ct);
        }
        else
        {
            using var ms2 = new MemoryStream(bytes);
            var text = await _ocr.ParseAsync(ms2, file.FileName, ct);
            elements = SplitIntoElements(text);
        }

        _log.LogInformation("Parsed into {Count} layout elements", elements.Count);

        var chunks = _chunker.Chunk(elements);
        _log.LogInformation("Produced {Count} chunks", chunks.Count);

        if (chunks.Count == 0)
            throw new InvalidOperationException(
                $"No chunks produced for '{file.FileName}'. " +
                "OCR may have timed out or returned empty output. " +
                "Check VLLM logs and retry.");

        // Assign a local document ID (no longer registered in RAGFlow)
        var documentId = Guid.NewGuid().ToString("N");
        _log.LogInformation("Assigned local document {DocId}", documentId);

        // Cache original file so reparse can re-run the custom pipeline
        var cacheExt  = Path.GetExtension(file.FileName);
        var cachePath = Path.Combine(_env.WebRootPath, "doc-cache", documentId + cacheExt);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, bytes);

        if (ext == ".docx")
            await CacheDocxPreviewAsync(bytes, file.FileName, documentId);

        // Index chunks in the local vector store for our own hybrid retrieval
        await IndexChunksAsync(datasetId, documentId, file.FileName, chunks, category);

        return new IngestionResult(documentId, elements.Count, chunks.Count, chunks.Count);
    }

    // ── IngestBytesAsync (reparse / byte-array uploads) ──────────────────────
    public async Task<IngestionResult> IngestBytesAsync(
        string datasetId, byte[] bytes, string fileName, string contentType,
        string category = "General")
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        List<LayoutElement> elements;

        if (ext == ".pdf")
        {
            var (textEls, emptyPages) = TryExtractPdfText(bytes);

            if (emptyPages.Count == 0 && textEls.Count > 0)
            {
                // Fully digital PDF — no vision OCR needed
                _log.LogInformation("PDF has selectable text on all pages — skipping vision OCR.");
                elements = textEls;
            }
            else if (textEls.Count == 0)
            {
                // Pure scan or PdfPig failed entirely — full vision OCR
                _log.LogInformation("PDF has no selectable text — routing through vision OCR.");
                elements = await _ocrClient.ExtractLayoutFromPdfAsync(bytes);
            }
            else
            {
                // Mixed: some pages digital, some scanned
                _log.LogInformation(
                    "PDF is mixed ({T} text pages, {S} scanned pages) — merging.",
                    textEls.Count, emptyPages.Count);
                var ocrAll = await _ocrClient.ExtractLayoutFromPdfAsync(bytes);
                var ocrByPage = ocrAll.GroupBy(e => e.Page)
                                      .ToDictionary(g => g.Key, g => g.ToList());
                elements = [.. textEls];
                foreach (var pg in emptyPages)
                    if (ocrByPage.TryGetValue(pg, out var pgEls))
                        elements.AddRange(pgEls);
                elements = [.. elements.OrderBy(e => e.Page)];
            }
        }
        else if (contentType.StartsWith("image/"))
        {
            elements = await _ocrClient.ExtractLayoutAsync(bytes, contentType);
        }
        else
        {
            using var ms = new MemoryStream(bytes);
            var text = await _ocr.ParseAsync(ms, fileName, contentType);
            elements = SplitIntoElements(text);
        }

        _log.LogInformation("Parsed into {Count} layout elements", elements.Count);

        var chunks = _chunker.Chunk(elements);
        _log.LogInformation("Produced {Count} chunks", chunks.Count);

        if (chunks.Count == 0)
            throw new InvalidOperationException(
                $"No chunks produced for '{fileName}'. " +
                "OCR may have timed out or returned empty output. " +
                "Check VLLM logs and retry.");

        var documentId = Guid.NewGuid().ToString("N");

        var cachePath = Path.Combine(_env.WebRootPath, "doc-cache",
            documentId + Path.GetExtension(fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, bytes);

        if (ext == ".docx")
            await CacheDocxPreviewAsync(bytes, fileName, documentId);

        // Index chunks in the local vector store for our own hybrid retrieval
        await IndexChunksAsync(datasetId, documentId, fileName, chunks, category);

        return new IngestionResult(documentId, elements.Count, chunks.Count, chunks.Count);
    }

    // ── ReingestAsync (↻ reparse button) ─────────────────────────────────────
    public async Task ReingestAsync(string datasetId, string documentId, string fileName,
        string category = "")
    {
        var dir = Path.Combine(_env.WebRootPath, "doc-cache");
        var match = (Directory.Exists(dir)
            ? Directory.GetFiles(dir, documentId + ".*").FirstOrDefault()
            : null) ?? throw new InvalidOperationException(
                $"No cached file for document {documentId}. " +
                "Only documents uploaded after caching was enabled can be reparsed.");
        var bytes = await File.ReadAllBytesAsync(match);
        var ct = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf"          => "application/pdf",
            ".png"          => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _               => "application/octet-stream"
        };

        // Preserve the category from existing chunks before deleting them
        if (string.IsNullOrEmpty(category))
        {
            var existing = await _vectorStore.GetByDocumentAsync(documentId);
            category = existing.FirstOrDefault()?.Category ?? "General";
        }

        await _vectorStore.DeleteByDocumentAsync(documentId);
        try { File.Delete(match); } catch { }

        await IngestBytesAsync(datasetId, bytes, fileName, ct, category);
    }

    // ── Local vector index ────────────────────────────────────────────────────
    // Embeds every chunk with Ollama and stores the result in VectorChunkStore
    // so HybridRetriever can serve queries without calling RagFlow's retrieval API.
    private async Task IndexChunksAsync(
        string datasetId, string documentId, string documentName,
        List<IngestionChunk> chunks, string category = "General")
    {
        var stored = new List<Models.StoredChunk>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var embedding = await _embedder.EmbedAsync(chunk.Content);

            // Use a stable hash of the content as the ID so re-ingesting the
            // same content produces the same key and overwrites the old entry.
            var id = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(documentId + i)))
                [..16].ToLowerInvariant();

            stored.Add(new Models.StoredChunk(
                Id:           id,
                DatasetId:    datasetId,
                DocumentId:   documentId,
                DocumentName: documentName,
                Content:      chunk.Content,
                Embedding:    embedding,
                Keywords:     chunk.Keywords ?? [],
                Category:     category));

            _log.LogInformation(
                "Indexed chunk {I}/{Total} for {DocId} (embedding dim: {D})",
                i + 1, chunks.Count, documentId, embedding.Length);
        }

        await _vectorStore.AddRangeAsync(stored);
        _log.LogInformation(
            "Stored {N} chunks in local vector index for document {DocId}",
            stored.Count, documentId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<LayoutElement> SplitIntoElements(string text)
    {
        var result = new List<LayoutElement>();

        void AddParagraphs(string segment)
        {
            var paragraphs = segment.Split(
                ["\r\n\r\n", "\n\n", "\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim().Length > 20)
                .ToList();

            if (paragraphs.Count <= 1 && segment.Length > 2000)
            {
                paragraphs = [.. Enumerable
                    .Range(0, (segment.Length + 1999) / 2000)
                    .Select(i => segment.Substring(i * 2000, Math.Min(2000, segment.Length - i * 2000)))
                    .Where(p => !string.IsNullOrWhiteSpace(p) && p.Trim().Length > 20)];
            }

            foreach (var para in paragraphs)
            {
                var trimmed = para.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (trimmed.Length < 20 && trimmed.All(char.IsDigit)) continue;

                if (trimmed.StartsWith("##"))
                {
                    result.Add(new LayoutElement
                    {
                        Category = LayoutCategory.SectionHeader,
                        Text     = trimmed.TrimStart('#').Trim(),
                        Page     = 1,
                        Bbox     = new BBox(0, 0, 1000, 30)
                    });
                    continue;
                }

                var isHeading = trimmed.Length < 100
                    && !trimmed.Contains('\n')
                    && !trimmed.EndsWith('.')
                    && !trimmed.EndsWith(',');

                result.Add(new LayoutElement
                {
                    Category = isHeading ? LayoutCategory.SectionHeader : LayoutCategory.Text,
                    Text     = trimmed,
                    Page     = 1,
                    Bbox     = new BBox(0, 0, 1000, 50)
                });
            }
        }

        // Split on <table>...</table> blocks (emitted by ExtractDocx/ExtractXlsx),
        // keeping the matched tables in the output via a capturing group so
        // document order is preserved — otherwise a table would lose its
        // association with the section header that precedes it.
        var segments = System.Text.RegularExpressions.Regex.Split(
            text, @"(<table>[\s\S]*?</table>)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var segment in segments)
        {
            if (segment.TrimStart().StartsWith("<table>", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new LayoutElement
                {
                    Category = LayoutCategory.Table,
                    Text     = segment.Trim(),
                    Page     = 1,
                    Bbox     = new BBox(0, 0, 1000, 50)
                });
            }
            else
            {
                AddParagraphs(segment);
            }
        }

        return result.Count > 0 ? result :
        [
            new() { Category = LayoutCategory.Text, Text = text,
                    Page = 1, Bbox = new BBox(0, 0, 1000, 1000) }
        ];
    }
}
