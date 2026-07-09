using System.Security.Cryptography;
using System.Text;
using RagFlowApi.Models;

namespace RagFlowApi.Services;

/// <summary>
/// One-shot background service that runs at startup and embeds any document
/// chunks already stored in RagFlow but not yet in the local VectorChunkStore.
///
/// This covers documents ingested before the hybrid retriever was introduced.
/// After this service completes, all future ingestions go through
/// IngestionPipeline which indexes automatically — so this only ever runs once
/// per document.
///
/// Strategy:
///   For each user that has a DatasetId in users.json:
///     For each document in that RagFlow dataset:
///       If the document has NO chunks in vector_store.json → embed and index all its chunks.
///       If it already has any chunks → skip (assume fully indexed).
/// </summary>
public class VectorStoreMigrationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VectorChunkStore _vectorStore;
    private readonly UserStore _userStore;
    private readonly ILogger<VectorStoreMigrationService> _log;

    public VectorStoreMigrationService(
        IServiceScopeFactory scopeFactory,
        VectorChunkStore vectorStore,
        UserStore userStore,
        ILogger<VectorStoreMigrationService> log)
    {
        _scopeFactory = scopeFactory;
        _vectorStore  = vectorStore;
        _userStore    = userStore;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the app finish starting before we start hammering the
        // embedding server and RagFlow.
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        _log.LogInformation("=== VectorStore migration starting ===");

        // RagFlowService and OllamaEmbeddingClient are transient (AddHttpClient),
        // so resolve them inside an explicit scope.
        using var scope   = _scopeFactory.CreateScope();
        var ragflow       = scope.ServiceProvider.GetRequiredService<RagFlowService>();
        var embedder      = scope.ServiceProvider.GetRequiredService<OllamaEmbeddingClient>();

        var users = await _userStore.GetAllAsync();

        // Each admin has their own dataset; non-admins share the admin's.
        // Migrate each unique DatasetId once.
        var datasets = users
            .Where(u => !string.IsNullOrWhiteSpace(u.DatasetId))
            .GroupBy(u => u.DatasetId!)
            .Select(g => (DatasetId: g.Key, Owner: g.First().Username))
            .ToList();

        if (datasets.Count == 0)
        {
            _log.LogInformation("No datasets found — nothing to migrate.");
            return;
        }

        foreach (var (datasetId, owner) in datasets)
        {
            if (stoppingToken.IsCancellationRequested) break;
            await MigrateDatasetAsync(ragflow, embedder, datasetId, owner, stoppingToken);
        }

        _log.LogInformation("=== VectorStore migration complete ===");
    }

    private async Task MigrateDatasetAsync(
        RagFlowService ragflow,
        OllamaEmbeddingClient embedder,
        string datasetId,
        string owner,
        CancellationToken ct)
    {
        // Build a set of document IDs that already have local embeddings.
        var existing      = await _vectorStore.GetByDatasetAsync(datasetId);
        var indexedDocIds = existing.Select(c => c.DocumentId).ToHashSet();

        List<DocumentItem> documents;
        try
        {
            documents = await ragflow.ListDocumentsAsync(datasetId, pageSize: 200);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not list documents for dataset {DS} (owner: {Owner}) — skipping.",
                datasetId, owner);
            return;
        }

        var toMigrate = documents
            .Where(d => !indexedDocIds.Contains(d.Id))
            .ToList();

        if (documents.Count == 0)
        {
            _log.LogInformation(
                "Dataset {DS} ({Owner}): no documents found in RagFlow — nothing to migrate.",
                datasetId, owner);
            return;
        }

        if (toMigrate.Count == 0)
        {
            _log.LogInformation(
                "Dataset {DS} ({Owner}): all {N} document(s) already indexed — nothing to do.",
                datasetId, owner, documents.Count);
            return;
        }

        _log.LogInformation(
            "Dataset {DS} ({Owner}): {M} of {Total} document(s) need indexing.",
            datasetId, owner, toMigrate.Count, documents.Count);

        foreach (var doc in toMigrate)
        {
            if (ct.IsCancellationRequested) break;
            await MigrateDocumentAsync(ragflow, embedder, datasetId, doc, ct);
        }
    }

    private async Task MigrateDocumentAsync(
        RagFlowService ragflow,
        OllamaEmbeddingClient embedder,
        string datasetId,
        DocumentItem doc,
        CancellationToken ct)
    {
        _log.LogInformation("Migrating '{Name}' ({Id})…", doc.Name, doc.Id);

        List<DocumentChunk> ragChunks;
        try
        {
            // Fetch up to 500 chunks — increase pageSize if documents are larger.
            ragChunks = await ragflow.ListChunksAsync(datasetId, doc.Id, pageSize: 500);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not fetch chunks for '{Name}' — skipping.", doc.Name);
            return;
        }

        if (ragChunks.Count == 0)
        {
            _log.LogWarning(
                "'{Name}' has no chunks in RagFlow (run status: {Status}) — skipping.",
                doc.Name, doc.RunStatus);
            return;
        }

        var stored = new List<StoredChunk>(ragChunks.Count);

        for (int i = 0; i < ragChunks.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var chunk = ragChunks[i];
            if (string.IsNullOrWhiteSpace(chunk.Content)) continue;

            float[] embedding;
            try
            {
                embedding = await embedder.EmbedAsync(chunk.Content);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Embed failed for chunk {I}/{Total} of '{Name}' — skipping chunk.",
                    i + 1, ragChunks.Count, doc.Name);
                continue;
            }

            // Use the same ID scheme as IngestionPipeline so a later re-ingest
            // overwrites the migrated entry cleanly.
            var id = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(doc.Id + i)))
                [..16].ToLowerInvariant();

            stored.Add(new StoredChunk(
                Id:           id,
                DatasetId:    datasetId,
                DocumentId:   doc.Id,
                DocumentName: doc.Name,
                Content:      chunk.Content,
                Embedding:    embedding,
                Keywords:     chunk.Keywords));

            _log.LogInformation(
                "  [{I}/{N}] '{Name}' — embedded (dim: {D})",
                i + 1, ragChunks.Count, doc.Name, embedding.Length);
        }

        if (stored.Count > 0)
        {
            await _vectorStore.AddRangeAsync(stored);
            _log.LogInformation(
                "Indexed {N}/{Total} chunks for '{Name}'.",
                stored.Count, ragChunks.Count, doc.Name);
        }
    }
}
