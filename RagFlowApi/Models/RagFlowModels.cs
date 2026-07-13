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
    string Department = "",
    string DocType = "",
    string Scope = "",
    string Status = ""
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
/// Scope and access-control values for the Department filter.
/// IsAdmin = true → no dept/scope filter applied; all chunks are visible.
/// </summary>
public record DeptFilter(bool IsAdmin, string? Department);

/// <summary>
/// A chunk stored in the local vector index.
/// Carries the pre-computed BGE-M3 embedding so retrieval needs no
/// re-embedding of the corpus at query time.
/// </summary>
public record StoredChunk(
    string Id,
    string DatasetId,
    string DocumentId,
    string DocumentName,
    string Content,
    float[] Embedding,
    List<string> Keywords,
    string Department = "",   // DEV | TEST | BA | HR | FINANCE
    string DocType    = "",   // Quy trình | Quy định | …
    string Scope      = "",   // Toàn công ty | Nội bộ phòng ban | Ban lãnh đạo
    string Status     = ""    // Đang hiệu lực | Hết hiệu lực | Bản nháp
);