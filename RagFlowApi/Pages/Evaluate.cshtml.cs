using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RagFlowApi.Models;
using RagFlowApi.Services;
using System.Text;

namespace RagFlowApi.Pages;

[Authorize(Roles = "admin")]
public class EvaluateModel : PageModel
{
    private readonly RagFlowService    _svc;
    private readonly RagasService      _ragas;
    private readonly UserContext       _userContext;
    private readonly EvalQuestionLoader _loader;
    private readonly ILogger<EvaluateModel> _log;

    public List<EvalQuestion> Questions    { get; private set; } = [];
    public List<EvalResult>   Results      { get; private set; } = [];
    public string?            ErrorMessage { get; private set; }
    public bool               HasRun       { get; private set; }

    public double AvgFaithfulness    => SafeAvg(Results, r => r.Faithfulness);
    public double AvgAnswerRelevancy => SafeAvg(Results, r => r.AnswerRelevancy);

    public EvaluateModel(
        RagFlowService svc,
        RagasService ragas,
        UserContext userContext,
        EvalQuestionLoader loader,
        ILogger<EvaluateModel> log)
    {
        _svc         = svc;
        _ragas       = ragas;
        _userContext = userContext;
        _loader      = loader;
        _log         = log;
    }

    public async Task OnGetAsync() => Questions = await _loader.LoadAsync();

    public async Task<IActionResult> OnPostRunAsync()
    {
        Questions = await _loader.LoadAsync();
        if (Questions.Count == 0)
        {
            ErrorMessage = "No questions found in eval_questions.json.";
            return Page();
        }

        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        HasRun = true;

        foreach (var q in Questions)
        {
            var result = new EvalResult { Id = q.Id, Question = q.Question };
            try
            {
                var completionJson = await _svc.GetAnswerAsync(datasetId, q.Question);
                var raw           = RagFlowService.ExtractAnswerFromJson(completionJson);
                result.Answer     = string.IsNullOrEmpty(raw) ? "(could not parse answer)" : raw;
                var chunks        = RagFlowService.ParseCompletionChunks(completionJson);
                result.ChunkCount = chunks.Count;
                var contextTexts  = chunks.Select(c => c.Content).ToList();

                var faithfulnessTask = _ragas.ScoreFaithfulnessAsync(result.Answer, contextTexts);
                var relevancyTask    = _ragas.ScoreAnswerRelevancyAsync(q.Question, result.Answer);
                await Task.WhenAll(faithfulnessTask, relevancyTask);

                result.Faithfulness    = faithfulnessTask.Result;
                result.AnswerRelevancy = relevancyTask.Result;

                await Task.Delay(10000);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Eval failed for question {Id}", q.Id);
                result.ErrorMessage = ex.Message;
            }

            Results.Add(result);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostExportAsync()
    {
        Questions = await _loader.LoadAsync();
        var datasetId = await _userContext.GetSharedDatasetIdAsync();
        var results = new List<EvalResult>();

        foreach (var q in Questions)
        {
            var result = new EvalResult { Id = q.Id, Question = q.Question };
            try
            {
                var completionJson    = await _svc.GetAnswerAsync(datasetId, q.Question);
                var rawExport        = RagFlowService.ExtractAnswerFromJson(completionJson);
                result.Answer         = string.IsNullOrEmpty(rawExport) ? "(could not parse answer)" : rawExport;
                var chunks            = RagFlowService.ParseCompletionChunks(completionJson);
                result.ChunkCount     = chunks.Count;
                var ctxTexts          = chunks.Select(c => c.Content).ToList();

                var ft = _ragas.ScoreFaithfulnessAsync(result.Answer, ctxTexts);
                var rt = _ragas.ScoreAnswerRelevancyAsync(q.Question, result.Answer);
                await Task.WhenAll(ft, rt);
                result.Faithfulness    = ft.Result;
                result.AnswerRelevancy = rt.Result;
            }
            catch (Exception ex) { result.ErrorMessage = ex.Message; }

            results.Add(result);
        }

        var bytes = Encoding.UTF8.GetBytes(BuildCsv(results));
        return new FileContentResult(bytes, "text/csv")
        {
            FileDownloadName = $"ragas_eval_{DateTime.Now:yyyyMMdd_HHmm}.csv"
        };
    }

    private static string BuildCsv(List<EvalResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ID,Question,Answer,Chunks,Faithfulness,AnswerRelevancy,Avg,FaithfulnessReason,RelevancyReason,Error");
        foreach (var r in results)
        {
            sb.Append(r.Id);                                         sb.Append(',');
            sb.Append(CsvEscape(r.Question));                        sb.Append(',');
            sb.Append(CsvEscape(r.Answer));                          sb.Append(',');
            sb.Append(r.ChunkCount);                                 sb.Append(',');
            sb.Append(r.Faithfulness?.Display ?? "");                sb.Append(',');
            sb.Append(r.AnswerRelevancy?.Display ?? "");             sb.Append(',');
            sb.Append(r.AvgScore >= 0 ? r.AvgScore.ToString("F2") : ""); sb.Append(',');
            sb.Append(CsvEscape(r.Faithfulness?.Reason ?? ""));     sb.Append(',');
            sb.Append(CsvEscape(r.AnswerRelevancy?.Reason ?? ""));  sb.Append(',');
            sb.AppendLine(CsvEscape(r.ErrorMessage ?? ""));
        }
        return sb.ToString();
    }

    private static string CsvEscape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static double SafeAvg(List<EvalResult> results, Func<EvalResult, RagasScore?> selector)
    {
        var valid = results
            .Select(selector)
            .Where(s => s is not null && !s.IsError)
            .Select(s => s!.Score)
            .ToList();
        return valid.Count > 0 ? valid.Average() : -1;
    }
}
