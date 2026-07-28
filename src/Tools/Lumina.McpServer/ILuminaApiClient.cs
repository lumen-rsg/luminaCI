using System.Text.Json.Nodes;

namespace Lumina.McpServer;

public interface ILuminaApiClient
{
    Task<JsonNode> GetDataAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<JsonNode> PostDataAsync(
        string path,
        object? body,
        CancellationToken cancellationToken = default);
}
