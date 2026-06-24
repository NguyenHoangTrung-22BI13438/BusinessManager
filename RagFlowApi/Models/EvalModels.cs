using RagFlowApi.Services;

namespace RagFlowApi.Models;

/// <summary>
/// One row in eval_questions.json.
/// ground_truth is optional — only needed for context recall (not implemented here).
/// </summary>
public class EvalQuestion
{
    public int    Id           { get; set; }
    public string Question     { get; set; } = "";
    public string? GroundTruth { get; set; }  // optional, for future context recall metric
}

/// <summary>
/// Result of running both RAGAS metrics on one question.
/// Produced by Evaluate.cshtml.cs, consumed by the Razor view.
/// </summary>
public class EvalResult
{
    public int     Id                  { get; set; }
    public string  Question            { get; set; } = "";
    public string  Answer              { get; set; } = "";
    public int     ChunkCount          { get; set; }
    public RagasScore? Faithfulness    { get; set; }
    public RagasScore? AnswerRelevancy { get; set; }
    public string? ErrorMessage        { get; set; }

    // ── Convenience for Razor ──────────────────────────────────────────────
    public bool HasError => ErrorMessage is not null;

    public double AvgScore
    {
        get
        {
            if (Faithfulness is null || AnswerRelevancy is null) return -1;
            if (Faithfulness.IsError || AnswerRelevancy.IsError) return -1;
            return (Faithfulness.Score + AnswerRelevancy.Score) / 2.0;
        }
    }
}
