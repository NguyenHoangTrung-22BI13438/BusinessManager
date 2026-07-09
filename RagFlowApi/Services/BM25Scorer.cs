using System.Text.RegularExpressions;

namespace RagFlowApi.Services;

/// <summary>
/// Stateless BM25 scorer (Okapi BM25, k1=1.5, b=0.75).
///
/// Usage: call Score(query, documents) at retrieval time — no pre-built index.
/// Suitable for corpora up to a few thousand chunks; for larger datasets build
/// an inverted index instead.
///
/// Vietnamese note: words are space-separated so whitespace tokenisation works
/// correctly without a language-specific segmenter.
/// </summary>
public class BM25Scorer
{
    private const double K1 = 1.5;
    private const double B  = 0.75;

    /// <summary>
    /// Returns a raw BM25 score for each document in <paramref name="documents"/>
    /// given <paramref name="query"/>.  Scores are non-negative; higher = more relevant.
    /// </summary>
    public double[] Score(string query, List<string> documents)
    {
        if (documents.Count == 0) return [];

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0) return new double[documents.Count];

        var docTokens = documents.Select(Tokenize).ToList();
        var avgDocLen = docTokens.Average(d => (double)d.Count);
        int n = documents.Count;

        // Precompute IDF for each unique query term
        var idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in queryTerms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int df = docTokens.Count(d =>
                d.Any(t => t.Equals(term, StringComparison.OrdinalIgnoreCase)));
            // Add-one smoothed IDF (Robertson & Sparck Jones variant)
            idf[term] = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);
        }

        var scores = new double[n];
        for (int i = 0; i < n; i++)
        {
            var tokens = docTokens[i];
            double dl = tokens.Count;

            foreach (var term in queryTerms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int tf = tokens.Count(t =>
                    t.Equals(term, StringComparison.OrdinalIgnoreCase));
                if (tf == 0) continue;

                double num = tf * (K1 + 1.0);
                double den = tf + K1 * (1.0 - B + B * dl / avgDocLen);
                scores[i] += idf[term] * (num / den);
            }
        }

        return scores;
    }

    private static List<string> Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
             .Where(t => t.Length > 1)
             .ToList();
}
