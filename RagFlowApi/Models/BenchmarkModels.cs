namespace RagFlowApi.Models;

// ── BM25 Weight Sweep ─────────────────────────────────────────────────────────

public record WeightSweepRow(
    double Bm25Weight,
    double VectorWeight,
    // ── Retrieval-level proxies (from similarity scores, no extra LLM calls) ──
    double AvgPrecisionProxy,   // fraction of returned chunks with combined score > 0.5
    double AvgRecallProxy,      // avg chunks returned / total corpus chunks
    // ── RAGAS (LLM-judged) ────────────────────────────────────────────────────
    double AvgFaithfulness,     // are answer claims grounded in context?
    double AvgRelevancy,        // does the answer address the question?
    int    ParsedCount,
    int    TotalCount)
{
    public double F1Proxy =>
        AvgPrecisionProxy + AvgRecallProxy > 0
            ? 2 * AvgPrecisionProxy * AvgRecallProxy / (AvgPrecisionProxy + AvgRecallProxy)
            : 0;

    // Overall quality score across all metrics that were computed
    public double AvgCombined
    {
        get
        {
            var vals = new List<double> { AvgPrecisionProxy, AvgRecallProxy };
            if (AvgFaithfulness >= 0) vals.Add(AvgFaithfulness);
            if (AvgRelevancy    >= 0) vals.Add(AvgRelevancy);
            return vals.Count > 0 ? vals.Average() : -1;
        }
    }

    public string ParsedLabel => $"{ParsedCount}/{TotalCount}";
}

public class WeightSweepResult
{
    public List<WeightSweepRow> Rows    { get; init; } = [];
    public int                  QCount  { get; init; }
    public double[]             Weights { get; init; } = [];
    public string?              Error   { get; set; }
}

// ── Semantic Paraphrase Proof ──────────────────────────────────────────────────

public class SemanticProofResult
{
    public string           Question         { get; init; } = "";
    public string           Paraphrase       { get; init; } = "";
    public List<RagChunk>   OriginalChunks   { get; init; } = [];
    public List<RagChunk>   ParaphraseChunks { get; init; } = [];
    public List<string>     OverlappingIds   { get; init; } = [];
    public double           JaccardOverlap   { get; init; }
    public string?          Error            { get; set; }
}

// ── Chunking Strategy Comparison ──────────────────────────────────────────────

public class ChunkCompareRow
{
    public string Strategy           { get; init; } = "";
    public int    ChunkCount         { get; init; }
    public double AvgLen             { get; init; }
    public double MinLen             { get; init; }
    public double MaxLen             { get; init; }
    public bool   PreservesStructure { get; init; }
    public List<string> Samples      { get; init; } = [];
}

public class ChunkingComparisonResult
{
    public ChunkCompareRow Layout { get; init; } = new();
    public ChunkCompareRow Naive  { get; init; } = new();
    public string?         Error  { get; set; }
}

// ── OCR CER / WER ─────────────────────────────────────────────────────────────

public class OcrBenchmarkResult
{
    public double  Cer       { get; init; }
    public double  Wer       { get; init; }
    public int     RefChars  { get; init; }
    public int     HypChars  { get; init; }
    public int     RefWords  { get; init; }
    public int     HypWords  { get; init; }
    public int     CharEdits { get; init; }
    public int     WordEdits { get; init; }
    public string? Error     { get; set; }

    public string CerDisplay => Error is null ? $"{Cer:P1}" : "ERR";
    public string WerDisplay => Error is null ? $"{Wer:P1}" : "ERR";
    public string CerClass   => Cer < 0.05 ? "score--high" : Cer < 0.15 ? "score--mid" : "score--low";
    public string WerClass   => Wer < 0.05 ? "score--high" : Wer < 0.15 ? "score--mid" : "score--low";
}

// ── Scale Metrics ──────────────────────────────────────────────────────────────

public class ScaleMetrics
{
    public int    TotalChunks       { get; init; }
    public int    UniqueDocuments   { get; init; }
    public int    UniqueDatasets    { get; init; }
    public long   StoreSizeBytes    { get; init; }
    public double AvgCharsPerChunk  { get; init; }
    public double EstQueryMs        { get; init; }
    public string StoreSizeDisplay  => StoreSizeBytes < 1024 * 1024
        ? $"{StoreSizeBytes / 1024.0:F1} KB"
        : $"{StoreSizeBytes / (1024.0 * 1024):F1} MB";
}
