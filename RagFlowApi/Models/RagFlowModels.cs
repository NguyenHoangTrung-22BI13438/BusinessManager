namespace RagFlowApi.Models;

// ── Settings ──────────────────────────────────────────────────────────────────
public class RagFlowSettings
{
    public string BaseUrl     { get; set; } = string.Empty;
    public string ApiKey      { get; set; } = string.Empty;
    public string AssistantId { get; set; } = string.Empty;
    public string DatasetId   { get; set; } = string.Empty;
}

// ── Response models ───────────────────────────────────────────────────────────

// ── Ingestion models (custom pipeline) ───────────────────────────────────────
public record IngestionResult(
    string DocumentId,
    int ElementCount,
    int ChunksProduced,
    int ChunksPushed
);

public record IngestionChunk(
    string Content,
    string ContentType,          // "text" | "table" | "formula" | "picture"
    int PageNumber,
    BBox? Bbox,
    string? SectionPath,
    List<string>? Keywords = null
);

// Session item shown in sidebar
public record SessionItem(string Id, string Name);

// A single message in chat history
public record ChatMessage(string Role, string Content, string Id = "")
{
    public List<RagChunk> Chunks { get; init; } = [];
}

public record RatingEntry(
    string SessionId,
    string MessageId,
    string Username,
    bool Positive,
    DateTime Timestamp
);

public record RagChunk(
    string Id,
    string Content,
    string DocumentId,
    string DocumentName,
    string? ImageId,
    double Similarity
);
public record DocumentItem(
    string Id,
    string Name,
    long Size,
    string Type,
    string RunStatus,    // UNSTART | RUNNING | DONE | FAIL | CANCEL
    int ChunkCount,
    int TokenCount,
    double Progress,     // 0.0–1.0 while parsing
    long CreateTime,     // unix ms
    string Category = "General"
);
public record DocumentChunk(
    string Id,
    string Content,
    string DocumentId,
    bool Available,
    string? ImageId,
    List<string> Keywords
);

/// <summary>
/// A chunk stored in the local vector index (VectorChunkStore).
/// Carries the pre-computed BGE-M3 embedding so retrieval needs no
/// re-embedding of the corpus at query time.
/// </summary>
public record StoredChunk(
    string Id,           // stable content-hash used as the dictionary key
    string DatasetId,    // filters chunks to the right user's knowledge base
    string DocumentId,
    string DocumentName,
    string Content,
    float[] Embedding,   // BGE-M3 dense vector (~1024 dims)
    List<string> Keywords,
    string Category = "General"  // department tag; controls which users can retrieve this chunk
);