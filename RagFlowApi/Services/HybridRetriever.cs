using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Retrieves the most relevant chunks for a query by combining:
///   - Dense retrieval  : cosine similarity between BGE-M3 embeddings
///   - Sparse retrieval : BM25 keyword score
///
/// Both score arrays are min-max normalised to [0,1] before combining so the
/// weight parameter has a consistent meaning regardless of corpus size.
///
/// Default weight (0.3 BM25 / 0.7 vector) matches the old RagFlow assistant
/// config; expose Retrieve(…, bm25Weight) to let callers tune it.
/// </summary>
public class HybridRetriever
{
    private readonly OllamaEmbeddingClient _embedder;
    private readonly VectorChunkStore _store;
    private readonly BM25Scorer _bm25;
    private readonly ILogger<HybridRetriever> _log;

    public HybridRetriever(
        OllamaEmbeddingClient embedder,
        VectorChunkStore store,
        BM25Scorer bm25,
        ILogger<HybridRetriever> log)
    {
        _embedder = embedder;
        _store    = store;
        _bm25     = bm25;
        _log      = log;
    }

    /// <param name="question">Natural-language query.</param>
    /// <param name="datasetId">Filter to only chunks from this dataset.</param>
    /// <param name="topN">Maximum number of chunks to return.</param>
    /// <param name="bm25Weight">
    ///   Weight for the BM25 score (0–1).
    ///   Vector weight = 1 – bm25Weight.
    ///   0.0 = pure vector, 1.0 = pure BM25.
    /// </param>
    /// <param name="similarityThreshold">
    ///   Minimum combined score; chunks below this are discarded.
    /// </param>
    public async Task<List<RagChunk>> RetrieveAsync(
        string question,
        string datasetId,
        int    topN                = 8,
        double bm25Weight          = 0.3,
        double similarityThreshold = 0.2,
        IReadOnlyList<string>? allowedCategories = null)
    {
        var chunks = await _store.GetByDatasetAsync(datasetId, allowedCategories);
        if (chunks.Count == 0)
        {
            _log.LogWarning(
                "No indexed chunks found for dataset {Id}. " +
                "Re-ingest documents to populate the local vector store.", datasetId);
            return [];
        }

        // ── 1. Embed the query ────────────────────────────────────────────────
        var qEmb = await _embedder.EmbedAsync(question);

        // ── 2. Vector scores (cosine similarity) ─────────────────────────────
        var vectorRaw = chunks
            .Select(c => CosineSimilarity(qEmb, c.Embedding))
            .ToArray();

        // ── 3. BM25 scores ───────────────────────────────────────────────────
        var bm25Raw = _bm25.Score(question, chunks.Select(c => c.Content).ToList());

        // ── 4. Normalise both to [0,1] then combine ──────────────────────────
        var vectorNorm = MinMaxNorm(vectorRaw);
        var bm25Norm   = MinMaxNorm(bm25Raw);

        double vectorWeight = 1.0 - bm25Weight;
        var combined = vectorNorm
            .Zip(bm25Norm, (v, b) => vectorWeight * v + bm25Weight * b)
            .ToArray();

        // ── 5. Filter, rank, take top-N ──────────────────────────────────────
        return combined
            .Select((score, i) => (score, chunk: chunks[i]))
            .Where(x => x.score >= similarityThreshold)
            .OrderByDescending(x => x.score)
            .Take(topN)
            .Select(x => new RagChunk(
                Id:           x.chunk.Id,
                Content:      x.chunk.Content,
                DocumentId:   x.chunk.DocumentId,
                DocumentName: x.chunk.DocumentName,
                ImageId:      null,
                Similarity:   Math.Round(x.score, 4)))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static double[] MinMaxNorm(double[] scores)
    {
        if (scores.Length == 0) return scores;
        double min = scores.Min(), max = scores.Max();
        double range = max - min;
        if (range == 0) return new double[scores.Length]; // all identical → all 0
        return scores.Select(s => (s - min) / range).ToArray();
    }
}
