using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Bulk;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// Replaces the flat vector_store.json file with Elasticsearch.
/// Provides KNN (HNSW) vector search and BM25 text search independently,
/// which HybridRetriever then combines.
/// </summary>
public sealed class ElasticsearchChunkStore
{
    private readonly ElasticsearchClient _es;
    private readonly ILogger<ElasticsearchChunkStore> _log;
    private const string Idx = "rag_chunks";

    public ElasticsearchChunkStore(IConfiguration config, ILogger<ElasticsearchChunkStore> log)
    {
        _log = log;
        var url = config["Elasticsearch:Url"] ?? "http://localhost:1200";
        _es = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(url)));
        EnsureIndexAsync().GetAwaiter().GetResult();
    }

    // ── Index management ──────────────────────────────────────────────────────

    private async Task EnsureIndexAsync()
    {
        try
        {
            var exists = await _es.Indices.ExistsAsync(Idx);
            if (exists.Exists) return;

            var props = new Properties
            {
                { "datasetId",    new KeywordProperty() },
                { "documentId",   new KeywordProperty() },
                { "documentName", new KeywordProperty() },
                { "content",      new TextProperty() },
                { "embedding",    new DenseVectorProperty { Dims = 1024, Index = true } },
                { "keywords",     new KeywordProperty() },
                { "department",   new KeywordProperty() },
                { "docType",      new KeywordProperty() },
                { "scope",        new KeywordProperty() },
                { "status",       new KeywordProperty() },
            };

            await _es.Indices.CreateAsync(new Elastic.Clients.Elasticsearch.IndexManagement.CreateIndexRequest(Idx)
            {
                Mappings = new TypeMapping { Properties = props }
            });
            _log.LogInformation("Created Elasticsearch index '{Idx}'", Idx);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to ensure Elasticsearch index '{Idx}'", Idx);
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task AddRangeAsync(IEnumerable<StoredChunk> chunks)
    {
        var docs = chunks.Select(ChunkDoc.From).ToList();
        if (docs.Count == 0) return;

        var ops = docs.SelectMany<ChunkDoc, IBulkOperation>(d =>
        [
            new BulkIndexOperation<ChunkDoc>(d) { Id = d.Id }
        ]).ToList();

        var res = await _es.BulkAsync(new BulkRequest(Idx) { Operations = ops });
        if (res.Errors)
            _log.LogWarning("ES bulk index had errors: {N}", res.ItemsWithErrors.Count());
    }

    public async Task DeleteByDocumentAsync(string documentId)
    {
        await _es.DeleteByQueryAsync(new Elastic.Clients.Elasticsearch.DeleteByQueryRequest(Idx)
        {
            Query = new TermQuery("documentId") { Value = documentId }
        });
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<List<StoredChunk>> GetByDatasetAsync(
        string datasetId,
        DeptFilter? filter = null)
    {
        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size  = 10000,
            Query = BuildFilter(datasetId, filter)
        });
        return res.Documents.Select(d => d.ToChunk()).ToList();
    }

    public async Task<List<StoredChunk>> GetByDocumentAsync(string documentId)
    {
        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size  = 10000,
            Query = new TermQuery("documentId") { Value = documentId }
        });
        return res.Documents.Select(d => d.ToChunk()).ToList();
    }

    public async Task<List<StoredChunk>> GetAllAsync()
    {
        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size  = 10000,
            Query = new MatchAllQuery()
        });
        return res.Documents.Select(d => d.ToChunk()).ToList();
    }

    // Returns { department → count } for the admin document list sidebar.
    public async Task<Dictionary<string, long>> GetDepartmentCountsAsync()
    {
        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size         = 0,
            Aggregations = new Dictionary<string, Aggregation>
            {
                { "depts", new TermsAggregation("depts") { Field = "department", Size = 20 } }
            }
        });

        var result = new Dictionary<string, long>();
        if (res.Aggregations?.TryGetValue("depts", out var agg) == true
            && agg is StringTermsAggregate terms)
            foreach (var b in terms.Buckets)
                result[b.Key.ToString()] = b.DocCount;

        return result;
    }

    public async Task<long> GetStoreSizeBytesAsync()
    {
        try
        {
            var stats = await _es.Indices.StatsAsync(s => s.Indices(Idx));
            return stats.Indices?[Idx]?.Total?.Store?.SizeInBytes ?? 0;
        }
        catch { return 0; }
    }

    // ── Hybrid search ─────────────────────────────────────────────────────────

    public async Task<List<(StoredChunk Chunk, double Score)>> SearchKnnAsync(
        string datasetId, float[] queryVector, int k,
        DeptFilter? filter = null)
    {
        var filters = BuildFilterList(datasetId, filter);

        var knnQuery = new KnnQuery
        {
            Field         = "embedding",
            QueryVector   = queryVector,
            k             = k,
            NumCandidates = k * 5,
            Filter        = filters
        };

        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size = k,
            Knn  = [knnQuery]
        });

        return res.Hits
            .Select(h => (h.Source!.ToChunk(), (double)(h.Score ?? 0)))
            .ToList();
    }

    public async Task<List<(StoredChunk Chunk, double Score)>> SearchBm25Async(
        string datasetId, string text, int k,
        DeptFilter? filter = null)
    {
        var filters = BuildFilterList(datasetId, filter);

        Query query = filters.Count == 0
            ? new MatchQuery("content") { Query = text }
            : new BoolQuery
            {
                Must   = [new MatchQuery("content") { Query = text }],
                Filter = filters
            };

        var res = await _es.SearchAsync<ChunkDoc>(new SearchRequest(Idx)
        {
            Size  = k,
            Query = query
        });

        return res.Hits
            .Select(h => (h.Source!.ToChunk(), (double)(h.Score ?? 0)))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Query BuildFilter(string datasetId, DeptFilter? filter)
    {
        var filters = BuildFilterList(datasetId, filter);
        if (filters.Count == 1) return filters[0];
        return new BoolQuery { Filter = filters };
    }

    private static List<Query> BuildFilterList(string datasetId, DeptFilter? filter)
    {
        var filters = new List<Query>
        {
            new TermQuery("datasetId") { Value = datasetId }
        };

        // Admin sees everything; no further filtering needed.
        if (filter is null || filter.IsAdmin) return filters;

        // Non-admin: only Đang hiệu lực documents
        filters.Add(new TermQuery("status") { Value = "Đang hiệu lực" });

        // Visible if: scope = "Toàn công ty"  OR  department = user's dept
        var shouldClauses = new List<Query>
        {
            new TermQuery("scope") { Value = "Toàn công ty" }
        };
        if (!string.IsNullOrWhiteSpace(filter.Department))
            shouldClauses.Add(new TermQuery("department") { Value = filter.Department });

        filters.Add(new BoolQuery { Should = shouldClauses, MinimumShouldMatch = 1 });

        return filters;
    }

    // ── ES document model ─────────────────────────────────────────────────────

    private sealed class ChunkDoc
    {
        public string       Id           { get; set; } = "";
        public string       DatasetId    { get; set; } = "";
        public string       DocumentId   { get; set; } = "";
        public string       DocumentName { get; set; } = "";
        public string       Content      { get; set; } = "";
        public float[]      Embedding    { get; set; } = [];
        public List<string> Keywords     { get; set; } = [];
        public string       Department   { get; set; } = "";
        public string       DocType      { get; set; } = "";
        public string       Scope        { get; set; } = "";
        public string       Status       { get; set; } = "";

        public static ChunkDoc From(StoredChunk c) => new()
        {
            Id           = c.Id,
            DatasetId    = c.DatasetId,
            DocumentId   = c.DocumentId,
            DocumentName = c.DocumentName,
            Content      = c.Content,
            Embedding    = c.Embedding,
            Keywords     = c.Keywords,
            Department   = c.Department,
            DocType      = c.DocType,
            Scope        = c.Scope,
            Status       = c.Status
        };

        public StoredChunk ToChunk() => new(
            Id, DatasetId, DocumentId, DocumentName,
            Content, Embedding, Keywords,
            Department, DocType, Scope, Status);
    }
}
