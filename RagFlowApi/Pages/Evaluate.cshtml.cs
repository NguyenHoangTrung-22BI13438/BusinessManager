using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;
using System.Text;
using System.Text.Json;

namespace RagFlowApi.Pages;

[Authorize(Roles = "admin")]
public class EvaluateModel : PageModel
{
    private readonly RagFlowService _svc;
    private readonly RagasService   _ragas;
    private readonly UserContext    _userContext;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<EvaluateModel> _log;

    // ── Page state ────────────────────────────────────────────────────────────
    public List<EvalQuestion> Questions  { get; private set; } = [];
    public List<EvalResult>   Results    { get; private set; } = [];
    public string?            ErrorMessage { get; private set; }
    public bool               HasRun     { get; private set; }

    // ── Summary stats (only valid after a run) ────────────────────────────────
    public double AvgFaithfulness    => SafeAvg(Results, r => r.Faithfulness);
    public double AvgAnswerRelevancy => SafeAvg(Results, r => r.AnswerRelevancy);

    public EvaluateModel(
        RagFlowService svc,
        RagasService ragas,
        UserContext userContext,
        IWebHostEnvironment env,
        ILogger<EvaluateModel> log)
    {
        _svc         = svc;
        _ragas       = ragas;
        _userContext = userContext;
        _env         = env;
        _log         = log;
    }

    // ── GET: load questions, no evaluation ───────────────────────────────────
    public async Task OnGetAsync()
    {
        Questions = await LoadQuestionsAsync();
    }

    // ── POST Run: evaluate all questions ─────────────────────────────────────
    public async Task<IActionResult> OnPostRunAsync()
    {
        Questions = await LoadQuestionsAsync();

        if (Questions.Count == 0)
        {
            ErrorMessage = "No questions found in eval_questions.json.";
            return Page();
        }

        var assistantId = await _userContext.EnsureAssistantAsync();
        HasRun = true;

        foreach (var q in Questions)
        {
            var result = new EvalResult
            {
                Id       = q.Id,
                Question = q.Question
            };

            string? sessionId = null;
            try
            {
                // 1. Create a fresh throwaway session so history doesn't bleed between questions
                sessionId = await _svc.CreateSessionAsync(assistantId, $"eval-{q.Id}");
                if (sessionId is null)
                    throw new InvalidOperationException("Could not create eval session.");

                // 2. Ask the question and get raw completion JSON
                var completionJson = await _svc.AskQuestionAsync(assistantId, sessionId, q.Question);

                // 3. Extract the answer text from the completion
                result.Answer = ExtractAnswerText(completionJson);

                // 4. Extract the retrieved chunks
                var chunks = RagFlowService.ParseCompletionChunks(completionJson);
                result.ChunkCount = chunks.Count;
                var contextTexts  = chunks.Select(c => c.Content).ToList();

                // 5. Score with RAGAS (parallel to save time)
                var faithfulnessTask    = _ragas.ScoreFaithfulnessAsync(result.Answer, contextTexts);
                var relevancyTask       = _ragas.ScoreAnswerRelevancyAsync(q.Question, result.Answer);
                await Task.WhenAll(faithfulnessTask, relevancyTask);

                result.Faithfulness = faithfulnessTask.Result;
                result.AnswerRelevancy = relevancyTask.Result;

                // Add this line:
                await Task.Delay(10000); // wait 5 seconds between questions
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Eval failed for question {Id}", q.Id);
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                // Always clean up the throwaway session
                if (sessionId is not null)
                {
                    try { await _svc.DeleteSessionAsync(assistantId, sessionId); }
                    catch { /* best-effort cleanup */ }
                }
            }

            Results.Add(result);
        }

        return Page();
    }

    // ── POST Export: download results as CSV ─────────────────────────────────
    public async Task<IActionResult> OnPostExportAsync()
    {
        // Re-run is not cheap — for export we expect results to be POSTed back
        // via hidden fields. However, a simpler approach: just redirect to run first.
        // Here we do a lightweight re-run and return the CSV directly.

        Questions = await LoadQuestionsAsync();
        var assistantId = await _userContext.EnsureAssistantAsync();
        var results = new List<EvalResult>();

        foreach (var q in Questions)
        {
            var result = new EvalResult { Id = q.Id, Question = q.Question };
            string? sessionId = null;
            try
            {
                sessionId = await _svc.CreateSessionAsync(assistantId, $"eval-export-{q.Id}");
                var completionJson = await _svc.AskQuestionAsync(assistantId, sessionId!, q.Question);
                result.Answer = ExtractAnswerText(completionJson);
                var chunks = RagFlowService.ParseCompletionChunks(completionJson);
                result.ChunkCount = chunks.Count;
                var ctxTexts = chunks.Select(c => c.Content).ToList();

                var ft = _ragas.ScoreFaithfulnessAsync(result.Answer, ctxTexts);
                var rt = _ragas.ScoreAnswerRelevancyAsync(q.Question, result.Answer);
                await Task.WhenAll(ft, rt);
                result.Faithfulness    = ft.Result;
                result.AnswerRelevancy = rt.Result;
            }
            catch (Exception ex) { result.ErrorMessage = ex.Message; }
            finally
            {
                if (sessionId is not null)
                    try { await _svc.DeleteSessionAsync(assistantId, sessionId); } catch { }
            }
            results.Add(result);
        }

        var csv = BuildCsv(results);
        var bytes = Encoding.UTF8.GetBytes(csv);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        return new FileContentResult(bytes, "text/csv")
        {
            FileDownloadName = $"ragas_eval_{stamp}.csv"
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<EvalQuestion>> LoadQuestionsAsync()
    {
        // Look for eval_questions.json in wwwroot first, then project root
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
                var json = await System.IO.File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<List<EvalQuestion>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? [];
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse eval_questions.json at {Path}", path);
                ErrorMessage = $"Could not parse eval_questions.json: {ex.Message}";
                return [];
            }
        }

        ErrorMessage = "eval_questions.json not found. " +
                       "Place it in wwwroot/ or the project root.";
        return [];
    }

    private static string ExtractAnswerText(string completionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(completionJson);
            var data = doc.RootElement.GetProperty("data");

            // RAGFlow puts the answer in data.answer
            if (data.TryGetProperty("answer", out var ans))
                return ans.GetString() ?? "(empty answer)";
        }
        catch { /* fall through */ }

        return "(could not parse answer)";
    }

    private static string BuildCsv(List<EvalResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ID,Question,Answer,Chunks,Faithfulness,AnswerRelevancy,Avg,FaithfulnessReason,RelevancyReason,Error");

        foreach (var r in results)
        {
            sb.Append(r.Id); sb.Append(',');
            sb.Append(CsvEscape(r.Question)); sb.Append(',');
            sb.Append(CsvEscape(r.Answer)); sb.Append(',');
            sb.Append(r.ChunkCount); sb.Append(',');
            sb.Append(r.Faithfulness?.Display ?? ""); sb.Append(',');
            sb.Append(r.AnswerRelevancy?.Display ?? ""); sb.Append(',');
            sb.Append(r.AvgScore >= 0 ? r.AvgScore.ToString("F2") : ""); sb.Append(',');
            sb.Append(CsvEscape(r.Faithfulness?.Reason ?? "")); sb.Append(',');
            sb.Append(CsvEscape(r.AnswerRelevancy?.Reason ?? "")); sb.Append(',');
            sb.AppendLine(CsvEscape(r.ErrorMessage ?? ""));
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }

    private static double SafeAvg(
        List<EvalResult> results,
        Func<EvalResult, RagasScore?> selector)
    {
        var valid = results
            .Select(selector)
            .Where(s => s is not null && !s.IsError)
            .Select(s => s!.Score)
            .ToList();

        return valid.Count > 0 ? valid.Average() : -1;
    }
}
