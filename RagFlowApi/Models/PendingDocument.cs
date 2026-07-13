namespace RagFlowApi.Models;

public enum PendingStatus { Pending, Approved, Rejected }

public class PendingDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string FilePath { get; set; } = string.Empty; // absolute path to stored bytes
    public PendingStatus Status { get; set; } = PendingStatus.Pending;
    public string Department { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
}