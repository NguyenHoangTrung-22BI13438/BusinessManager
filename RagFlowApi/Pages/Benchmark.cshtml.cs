using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;
using System.Text.Json;

namespace RagFlowApi.Pages;

[Authorize(Roles = "admin")]
public class BenchmarkModel : PageModel
{
    private readonly BenchmarkService _bench;
    private readonly UserContext      _ctx;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BenchmarkModel> _log;

    // ── Bound form fields ─────────────────────────────────────────────────────

    [BindProperty] public string? SemanticQuestion   { get; set; }
    [BindProperty] public string? SemanticParaphrase { get; set; }
    [BindProperty] public string? OcrReference       { get; set; }
    [BindProperty] public string? OcrHypothesis      { get; set; }
    [BindProperty] public int     NaiveChunkSize     { get; set; } = 500;

    // ── Page state (results) ──────────────────────────────────────────────────

    public WeightSweepResult?        SweepResult     { get; private set; }
    public SemanticProofResult?      SemanticResult  { get; private set; }
    public ChunkingComparisonResult? ChunkResult     { get; private set; }
    public OcrBenchmarkResult?       OcrResult       { get; private set; }
    public ScaleMetrics?             Scale           { get; private set; }
    public List<EvalQuestion>        Questions       { get; private set; } = [];
    public string?                   ActiveSection   { get; private set; }
    public string?                   ErrorMessage    { get; private set; }

    public BenchmarkModel(
        BenchmarkService bench,
        UserContext ctx,
        IWebHostEnvironment env,
        ILogger<BenchmarkModel> log)
    {
        _bench = bench;
        _ctx   = ctx;
        _env   = env;
        _log   = log;
    }

    public async Task OnGetAsync()
    {
        Questions = await LoadQuestionsAsync();
        Scale     = await _bench.GetScaleMetricsAsync();
    }

    // ── POST: BM25 weight sweep ───────────────────────────────────────────────

    public async Task<IActionResult> OnPostSweepAsync()
    {
        ActiveSection = "sweep";
        Questions     = await LoadQuestionsAsync();
        Scale         = await _bench.GetScaleMetricsAsync();

        if (Questions.Count == 0)
        {
            ErrorMessage = "No eval questions found (eval_questions.json). Add questions first.";
            return Page();
        }

        var datasetId = await _ctx.GetSharedDatasetIdAsync();
        SweepResult = await _bench.RunWeightSweepAsync(Questions, datasetId);
        return Page();
    }

    // ── POST: Semantic paraphrase proof ──────────────────────────────────────

    public async Task<IActionResult> OnPostSemanticAsync()
    {
        ActiveSection = "semantic";
        Questions     = await LoadQuestionsAsync();
        Scale         = await _bench.GetScaleMetricsAsync();

        var datasetId = await _ctx.GetSharedDatasetIdAsync();
        SemanticResult = await _bench.RunSemanticProofAsync(
            SemanticQuestion ?? "", SemanticParaphrase ?? "", datasetId);
        return Page();
    }

    // ── POST: Chunking comparison ─────────────────────────────────────────────

    public async Task<IActionResult> OnPostChunkingAsync()
    {
        ActiveSection = "chunking";
        Questions     = await LoadQuestionsAsync();
        Scale         = await _bench.GetScaleMetricsAsync();

        var datasetId = await _ctx.GetSharedDatasetIdAsync();
        ChunkResult   = await _bench.RunChunkingComparisonAsync(datasetId, NaiveChunkSize);
        return Page();
    }

    // ── POST: OCR CER / WER ──────────────────────────────────────────────────

    public async Task<IActionResult> OnPostOcrAsync()
    {
        ActiveSection = "ocr";
        Questions     = await LoadQuestionsAsync();
        Scale         = await _bench.GetScaleMetricsAsync();

        OcrResult = _bench.ComputeOcrMetrics(OcrReference ?? "", OcrHypothesis ?? "");
        return Page();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<EvalQuestion>> LoadQuestionsAsync()
    {
        var candidates = new[]
        {
            Path.Combine(_env.WebRootPath, "eval_questions.json"),
            Path.Combine(_env.ContentRootPath, "eval_questions.json")
        };

        foreach (var path in candidates)
        {
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                var text = await System.IO.File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<EvalQuestion>>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not parse eval_questions.json at {P}", path);
            }
        }
        return [];
    }
}
