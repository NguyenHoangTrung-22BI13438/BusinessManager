namespace RagFlowApi.Services;

public interface IParser
{
    Task<string> ParseAsync(Stream stream, string fileName, string contentType);
}