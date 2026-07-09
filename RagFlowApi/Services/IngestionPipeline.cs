using System.Text.Json;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

public class IngestionPipeline
{
    private readonly IParser _ocr;
    private readonly DotsOcrClient _ocrClient;
    private readonly LayoutChunker _chunker;
    private readonly OllamaEmbeddingClient _embedder;
    private readonly ElasticsearchChunkStore _chunkStore;
    private readonly ILogger<IngestionPipeline> _log;
    private readonly IWebHostEnvironment _env;

    public IngestionPipeline(
        IParser ocr, DotsOcrClient ocrClient,
        LayoutChunker chunker,
        OllamaEmbeddingClient embedder, ElasticsearchChunkStore chunkStore,
        ILogger<IngestionPipeline> log, IWebHostEnvironment env)
    {
        _ocr        = ocr;
        _ocrClient  = ocrClient;
        _chunker    = chunker;
        _embedder   = embedder;
        _chunkStore = chunkStore;
        _log        = log;
        _env        = env;
    }

    // Converts and caches a docx preview PDF alongside the original file cache.
    // Failure is non-fatal — the chunk viewer just falls back to no preview.
    private async Task CacheDocxPreviewAsync(byte[] docxBytes, string fileName, string documentId)
    {
        var pdfBytes = await LibreOfficeConverter.ConvertToPdfAsync(docxBytes, fileName);
        if (pdfBytes is null)
        {
            _log.LogWarning("docx→pdf conversion produced no output for {File}", fileName);
            return;
        }

        var pdfCachePath = Path.Combine(_env.WebRootPath, "doc-cache", documentId + ".pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(pdfCachePath)!);
        await File.WriteAllBytesAsync(pdfCachePath, pdfBytes);
    }

    // ── Smart PDF text extraction ─────────────────────────────────────────────
    // Uses PdfPig to pull selectable text from each page.
    // Returns (textElements, emptyPageNumbers).
    // emptyPageNumbers contains pages PdfPig found no text on — these need vision OCR.
    // Returns ([], []) on PdfPig failure — caller falls back to full OCR.
    private static (List<LayoutElement> Text, List<int> EmptyPages)
        TryExtractPdfText(byte[] pdfBytes)
    {
        try
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var textElements = new List<LayoutElement>();
            var emptyPages   = new List<int>();
            int page         = 0;

            foreach (var p in pdf.GetPages())
            {
                page++;
                var text = p.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    textElements.Add(new LayoutElement
                    {
                        Category = LayoutCategory.Text,
                        Text     = text,
                        Page     = page,
                        Bbox     = new BBox(0, 0, 1000, 1000)
                    });
                else
                    emptyPages.Add(page);
            }
            return (textElements, emptyPages);
        }
        catch { return ([], []); }
    }

    // Routes PDF bytes through the appropriate extraction strategy:
    // digital-only → PdfPig; pure scan → vision OCR; mixed → merge both.
    private async Task<List<LayoutElement>> ParsePdfElementsAsync(byte[] pdfBytes)
    {
        var (textEls, emptyPages) = TryExtractPdfText(pdfBytes);

        if (emptyPages.Count == 0 && textEls.Count > 0)
        {
            _log.LogInformation("PDF has selectable text on all pages — skipping vision OCR.");
            return textEls;
        }

        if (textEls.Count == 0)
        {
            _log.LogInformation("PDF has no selectable text — routing through vision OCR.");
            return await _ocrClient.ExtractLayoutFromPdfAsync(pdfBytes);
        }

        // Mixed: some pages digital, some scanned
        _log.LogInformation(
            "PDF is mixed ({T} text pages, {S} scanned pages) — merging.",
            textEls.Count, emptyPages.Count);
        var ocrAll = await _ocrClient.ExtractLayoutFromPdfAsync(pdfBytes);
        var ocrByPage = ocrAll.GroupBy(e => e.Page).ToDictionary(g => g.Key, g => g.ToList());
        var elements = new List<LayoutElement>(textEls);
        foreach (var pg in emptyPages)
            if (ocrByPage.TryGetValue(pg, out var pgEls))
                elements.AddRange(pgEls);
        return [.. elements.OrderBy(e => e.Page)];
    }

    // ── IngestAsync (new uploads via IFormFile) ───────────────────────────────
    public async Task<IngestionResult> IngestAsync(string datasetId, IFormFile file, string category = "General")
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var ct    = file.ContentType ?? "application/octet-stream";
        var ext   = Path.GetExtension(file.FileName).ToLowerInvariant();

        _log.LogInformation("Ingesting {File} ({Size} bytes) → dataset {DS}",
            file.FileName, bytes.Length, datasetId);

        List<LayoutElement> elements;
        if (ext == ".pdf")
            elements = await ParsePdfElementsAsync(bytes);
        else if (ct.StartsWith("image/"))
            elements = await _ocrClient.ExtractLayoutAsync(bytes, ct);
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

        var documentId = Guid.NewGuid().ToString("N");
        _log.LogInformation("Assigned local document {DocId}", documentId);

        var cacheExt  = Path.GetExtension(file.FileName);
        var cachePath = Path.Combine(_env.WebRootPath, "doc-cache", documentId + cacheExt);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllBytesAsync(cachePath, bytes);

        if (ext == ".docx")
            await CacheDocxPreviewAsync(bytes, file.FileName, documentId);

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
            elements = await ParsePdfElementsAsync(bytes);
        else if (contentType.StartsWith("image/"))
            elements = await _ocrClient.ExtractLayoutAsync(bytes, contentType);
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
            ".pdf"            => "application/pdf",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _                 => "application/octet-stream"
        };

        // Preserve the category from existing chunks before deleting them
        if (string.IsNullOrEmpty(category))
        {
            var existing = await _chunkStore.GetByDocumentAsync(documentId);
            category = existing.FirstOrDefault()?.Category ?? "General";
        }

        await _chunkStore.DeleteByDocumentAsync(documentId);
        try { File.Delete(match); } catch { }

        await IngestBytesAsync(datasetId, bytes, fileName, ct, category);
    }

    // ── Embed chunks into Elasticsearch for hybrid retrieval ──────────────────
    private async Task IndexChunksAsync(
        string datasetId, string documentId, string documentName,
        List<IngestionChunk> chunks, string category = "General")
    {
        var stored = new List<Models.StoredChunk>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk     = chunks[i];
            var embedding = await _embedder.EmbedAsync(chunk.Content);

            // Stable hash of (documentId + position) so re-ingesting the same content
            // produces the same ES document ID and overwrites rather than duplicates.
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

        await _chunkStore.AddRangeAsync(stored);
        _log.LogInformation(
            "Stored {N} chunks in Elasticsearch for document {DocId}",
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
