using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Hybrid retriever using Elasticsearch for both searches:
///   - KNN (HNSW)  : dense vector similarity via ES dense_vector index
///   - BM25        : sparse keyword match via ES standard text scoring
/// Both result sets are min-max normalised then combined with configurable weights.
/// </summary>
public class HybridRetriever
{
    private readonly OllamaEmbeddingClient    _embedder;
    private readonly ElasticsearchChunkStore  _store;
    private readonly ILogger<HybridRetriever> _log;

    public HybridRetriever(
        OllamaEmbeddingClient    embedder,
        ElasticsearchChunkStore  store,
        ILogger<HybridRetriever> log)
    {
        _embedder = embedder;
        _store    = store;
        _log      = log;
    }

    public async Task<List<RagChunk>> RetrieveAsync(
        string question,
        string datasetId,
        int    topN                = 8,
        double bm25Weight          = 0.3,
        double similarityThreshold = 0.2,
        IReadOnlyList<string>? allowedCategories = null)
    {
        // 1. Embed the query
        var qEmb = await _embedder.EmbedAsync(question);

        // 2. KNN and BM25 in parallel — fetch more candidates than topN for reranking
        int candidates = topN * 6;
        var knnTask  = _store.SearchKnnAsync(datasetId,  qEmb,     candidates, allowedCategories);
        var bm25Task = _store.SearchBm25Async(datasetId, question, candidates, allowedCategories);
        await Task.WhenAll(knnTask, bm25Task);

        var knnHits  = knnTask.Result;
        var bm25Hits = bm25Task.Result;

        if (knnHits.Count == 0 && bm25Hits.Count == 0)
        {
            _log.LogWarning("No chunks found in ES for dataset {Id}. Re-ingest documents.", datasetId);
            return [];
        }

        // 3. Normalise each score list to [0, 1]
        var knnNorm  = MinMaxNorm(knnHits .Select(x => x.Score).ToArray());
        var bm25Norm = MinMaxNorm(bm25Hits.Select(x => x.Score).ToArray());

        // 4. Merge into a single map, accumulating both scores per chunk
        var merged = new Dictionary<string, (StoredChunk Chunk, double Vec, double Bm25)>();

        for (int i = 0; i < knnHits.Count; i++)
        {
            var chunk = knnHits[i].Chunk;
            merged[chunk.Id] = (chunk, knnNorm[i], 0);
        }
        for (int i = 0; i < bm25Hits.Count; i++)
        {
            var chunk = bm25Hits[i].Chunk;
            merged[chunk.Id] = merged.TryGetValue(chunk.Id, out var ex)
                ? (chunk, ex.Vec, bm25Norm[i])
                : (chunk, 0, bm25Norm[i]);
        }

        // 5. Combine, threshold, rank, take top-N
        double vecWeight = 1.0 - bm25Weight;
        return merged.Values
            .Select(x => (Score: vecWeight * x.Vec + bm25Weight * x.Bm25, x.Chunk))
            .Where(x => x.Score >= similarityThreshold)
            .OrderByDescending(x => x.Score)
            .Take(topN)
            .Select(x => new RagChunk(
                Id:           x.Chunk.Id,
                Content:      x.Chunk.Content,
                DocumentId:   x.Chunk.DocumentId,
                DocumentName: x.Chunk.DocumentName,
                ImageId:      null,
                Similarity:   Math.Round(x.Score, 4)))
            .ToList();
    }

    private static double[] MinMaxNorm(double[] scores)
    {
        if (scores.Length == 0) return scores;
        double min = scores.Min(), max = scores.Max();
        double range = max - min;
        return range == 0
            ? scores.Select(_ => 0.5).ToArray()
            : scores.Select(s => (s - min) / range).ToArray();
    }
}
