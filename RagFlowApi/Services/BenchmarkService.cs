using System.Text.RegularExpressions;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Runs the five benchmark categories for the defense presentation:
///   1. BM25 weight sweep    – RAGAS scores across BM25Weight in [0,1]
///   2. Semantic proof       – Jaccard overlap between original vs paraphrase retrieval
///   3. Chunking comparison  – Layout-aware vs naive fixed-size chunking stats
///   4. OCR quality          – CER / WER computed from two pasted text blocks
///   5. Scale metrics        – Chunk count, store size, estimated query latency
/// </summary>
public class BenchmarkService
{
    private readonly HybridRetriever _retriever;
    private readonly RagFlowService  _ragflow;
    private readonly RagasService    _ragas;
    private readonly VectorChunkStore _store;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BenchmarkService> _log;

    // Weights tried in the sweep
    private static readonly double[] SweepWeights = [0.0, 0.2, 0.3, 0.5, 0.7, 1.0];

    public BenchmarkService(
        HybridRetriever retriever,
        RagFlowService  ragflow,
        RagasService    ragas,
        VectorChunkStore store,
        IWebHostEnvironment env,
        ILogger<BenchmarkService> log)
    {
        _retriever = retriever;
        _ragflow   = ragflow;
        _ragas     = ragas;
        _store     = store;
        _env       = env;
        _log       = log;
    }

    // ── 1. BM25 Weight Sweep ─────────────────────────────────────────────────

    public async Task<WeightSweepResult> RunWeightSweepAsync(
        List<EvalQuestion> questions, string datasetId)
    {
        if (questions.Count == 0)
            return new WeightSweepResult { Error = "No eval questions provided." };

        // Total indexed chunks — denominator for the recall proxy
        var allDatasetChunks = await _store.GetByDatasetAsync(datasetId);
        int totalChunks = allDatasetChunks.Count;

        var rows = new List<WeightSweepRow>();

        foreach (var w in SweepWeights)
        {
            double sumPrec = 0, sumRec = 0, sumF = 0, sumR = 0;
            int parsed = 0;

            foreach (var q in questions)
            {
                try
                {
                    var chunks = await _retriever.RetrieveAsync(
                        q.Question, datasetId, topN: 8, bm25Weight: w);

                    // ── Retrieval proxies (no extra LLM calls) ────────────────
                    // Precision proxy: fraction of returned chunks with score > 0.5
                    // (high combined score → confidently relevant)
                    int highConf = chunks.Count(c => c.Similarity > 0.5);
                    double precProxy = chunks.Count > 0
                        ? (double)highConf / chunks.Count : 0;

                    // Recall proxy: what fraction of the corpus did we retrieve?
                    // Higher vector weight → broader semantic net → more chunks retrieved
                    double recProxy = totalChunks > 0
                        ? (double)chunks.Count / totalChunks : 0;

                    sumPrec += precProxy;
                    sumRec  += recProxy;

                    // ── RAGAS (2 Gemini calls per question) ───────────────────
                    var contexts = chunks.Select(c => c.Content).ToList();
                    var answer   = await _ragflow.CallGeminiForAnswerAsync(q.Question, contexts);
                    var faith    = await _ragas.ScoreFaithfulnessAsync(answer, contexts);
                    var rel      = await _ragas.ScoreAnswerRelevancyAsync(q.Question, answer);

                    if (!faith.IsError && !rel.IsError)
                    {
                        sumF += faith.Score;
                        sumR += rel.Score;
                        parsed++;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Sweep weight={W} question={Q} failed", w, q.Id);
                }
            }

            int n = questions.Count;
            rows.Add(new WeightSweepRow(
                Bm25Weight:         w,
                VectorWeight:       1.0 - w,
                AvgPrecisionProxy:  n > 0 ? sumPrec / n : 0,
                AvgRecallProxy:     n > 0 ? sumRec  / n : 0,
                AvgFaithfulness:    parsed > 0 ? sumF / parsed : -1,
                AvgRelevancy:       parsed > 0 ? sumR / parsed : -1,
                ParsedCount:        parsed,
                TotalCount:         n));
        }

        return new WeightSweepResult
        {
            Rows    = rows,
            QCount  = questions.Count,
            Weights = SweepWeights
        };
    }

    // ── 2. Semantic Paraphrase Proof ─────────────────────────────────────────

    public async Task<SemanticProofResult> RunSemanticProofAsync(
        string question, string paraphrase, string datasetId)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new SemanticProofResult { Error = "Question is required." };
        if (string.IsNullOrWhiteSpace(paraphrase))
            return new SemanticProofResult { Error = "Paraphrase is required." };

        try
        {
            var origChunks  = await _retriever.RetrieveAsync(question,   datasetId, topN: 8);
            var paraChunks  = await _retriever.RetrieveAsync(paraphrase, datasetId, topN: 8);

            var origIds = origChunks.Select(c => c.Id).ToHashSet();
            var paraIds = paraChunks.Select(c => c.Id).ToHashSet();

            var intersection = origIds.Intersect(paraIds).ToList();
            var union        = origIds.Union(paraIds).Count();
            double jaccard   = union == 0 ? 0 : (double)intersection.Count / union;

            return new SemanticProofResult
            {
                Question          = question,
                Paraphrase        = paraphrase,
                OriginalChunks    = origChunks,
                ParaphraseChunks  = paraChunks,
                OverlappingIds    = intersection,
                JaccardOverlap    = Math.Round(jaccard, 4)
            };
        }
        catch (Exception ex)
        {
            return new SemanticProofResult { Error = ex.Message };
        }
    }

    // ── 3. Chunking Strategy Comparison ──────────────────────────────────────

    /// <summary>
    /// Compares layout-aware chunking (already run on documents in the store)
    /// vs a naive fixed-size approach applied to the concatenated chunk content.
    /// </summary>
    public async Task<ChunkingComparisonResult> RunChunkingComparisonAsync(
        string datasetId, int naiveChunkSize = 500)
    {
        try
        {
            var stored = await _store.GetByDatasetAsync(datasetId);
            if (stored.Count == 0)
                return new ChunkingComparisonResult
                    { Error = "No indexed chunks for this dataset. Re-ingest a document first." };

            // ── Layout-aware (real chunks from the store) ─────────────────────
            var layoutLens = stored.Select(c => c.Content.Length).ToList();
            var layoutRow  = new ChunkCompareRow
            {
                Strategy           = "Layout-Aware (ours)",
                ChunkCount         = stored.Count,
                AvgLen             = layoutLens.Average(),
                MinLen             = layoutLens.Min(),
                MaxLen             = layoutLens.Max(),
                PreservesStructure = true,
                Samples            = stored.Take(2)
                                          .Select(c => c.Content[..Math.Min(200, c.Content.Length)] + "…")
                                          .ToList()
            };

            // ── Naive fixed-size (simulate by splitting the same text) ─────────
            var allText    = string.Join("\n\n", stored.Select(c => c.Content));
            var naiveList  = NaiveChunk(allText, naiveChunkSize);
            var naiveLens  = naiveList.Select(s => s.Length).ToList();
            var naiveRow   = new ChunkCompareRow
            {
                Strategy           = $"Naive Fixed-Size ({naiveChunkSize} chars)",
                ChunkCount         = naiveList.Count,
                AvgLen             = naiveLens.Average(),
                MinLen             = naiveLens.Min(),
                MaxLen             = naiveLens.Max(),
                PreservesStructure = false,
                Samples            = naiveList.Take(2)
                                             .Select(s => s[..Math.Min(200, s.Length)] + "…")
                                             .ToList()
            };

            return new ChunkingComparisonResult { Layout = layoutRow, Naive = naiveRow };
        }
        catch (Exception ex)
        {
            return new ChunkingComparisonResult { Error = ex.Message };
        }
    }

    // ── 4. OCR CER / WER ─────────────────────────────────────────────────────

    public OcrBenchmarkResult ComputeOcrMetrics(string reference, string hypothesis)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return new OcrBenchmarkResult { Error = "Reference text is required." };
        if (string.IsNullOrWhiteSpace(hypothesis))
            return new OcrBenchmarkResult { Error = "Hypothesis (OCR output) text is required." };

        // Normalise whitespace
        var refN = NormaliseWhitespace(reference);
        var hypN = NormaliseWhitespace(hypothesis);

        // ── CER (character level) ────────────────────────────────────────────
        var refChars = refN.ToCharArray();
        var hypChars = hypN.ToCharArray();
        int charEdits = EditDistance(refChars, hypChars);
        double cer = refChars.Length == 0 ? 0 : (double)charEdits / refChars.Length;

        // ── WER (word level) ─────────────────────────────────────────────────
        var refWords = Tokenise(refN);
        var hypWords = Tokenise(hypN);
        int wordEdits = EditDistance(refWords, hypWords);
        double wer = refWords.Length == 0 ? 0 : (double)wordEdits / refWords.Length;

        return new OcrBenchmarkResult
        {
            Cer       = Math.Round(cer,  4),
            Wer       = Math.Round(wer,  4),
            RefChars  = refChars.Length,
            HypChars  = hypChars.Length,
            RefWords  = refWords.Length,
            HypWords  = hypWords.Length,
            CharEdits = charEdits,
            WordEdits = wordEdits
        };
    }

    // ── 5. Scale Metrics ─────────────────────────────────────────────────────

    public async Task<ScaleMetrics> GetScaleMetricsAsync()
    {
        var allChunks     = await _store.GetAllAsync();
        var totalChunks   = allChunks.Count;
        var uniqueDocs    = allChunks.Select(c => c.DocumentId).Distinct().Count();
        var uniqueDatasets = allChunks.Select(c => c.DatasetId).Distinct().Count();
        var avgChars      = totalChunks == 0 ? 0 : allChunks.Average(c => c.Content.Length);

        var path      = Path.Combine(_env.ContentRootPath, "vector_store.json");
        long fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;

        // Rough latency estimate: O(N) cosine scan on 1024-dim vectors (~0.01 ms/chunk)
        double estMs = totalChunks * 0.01;

        return new ScaleMetrics
        {
            TotalChunks      = totalChunks,
            UniqueDocuments  = uniqueDocs,
            UniqueDatasets   = uniqueDatasets,
            StoreSizeBytes   = fileSize,
            AvgCharsPerChunk = Math.Round(avgChars, 1),
            EstQueryMs       = Math.Round(estMs, 2)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> NaiveChunk(string text, int size)
    {
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += size)
        {
            int len = Math.Min(size, text.Length - i);
            chunks.Add(text.Substring(i, len));
        }
        return chunks;
    }

    private static string NormaliseWhitespace(string s) =>
        Regex.Replace(s.Trim(), @"\s+", " ");

    private static string[] Tokenise(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
             .Where(t => t.Length > 0)
             .ToArray();

    // Standard Levenshtein edit distance
    private static int EditDistance<T>(T[] source, T[] target) where T : IEquatable<T>
    {
        int m = source.Length, n = target.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        for (int i = 1; i <= m; i++)
        for (int j = 1; j <= n; j++)
        {
            dp[i, j] = source[i - 1].Equals(target[j - 1])
                ? dp[i - 1, j - 1]
                : 1 + Math.Min(dp[i - 1, j - 1], Math.Min(dp[i - 1, j], dp[i, j - 1]));
        }
        return dp[m, n];
    }
}
