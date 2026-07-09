using RagFlowApi.Models;
using System.Text.Json;

namespace RagFlowApi.Services;

public class EvalQuestionLoader
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<EvalQuestionLoader> _log;

    public EvalQuestionLoader(IWebHostEnvironment env, ILogger<EvalQuestionLoader> log)
    {
        _env = env;
        _log = log;
    }

    public async Task<List<EvalQuestion>> LoadAsync()
    {
        var candidates = new[]
        {
            Path.Combine(_env.WebRootPath,    "eval_questions.json"),
            Path.Combine(_env.ContentRootPath, "eval_questions.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var text = await File.ReadAllTextAsync(path);
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
