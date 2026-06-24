using RagFlowApi.Services;

namespace RagFlowApi.Models;

public class FormTemplate
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N");
    public string Name        { get; set; } = string.Empty;
    public string FileName    { get; set; } = string.Empty;
    public string UploadedBy  { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public List<FormField> Fields { get; set; } = [];
}
