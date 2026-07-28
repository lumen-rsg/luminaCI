using System.Text.Json.Nodes;
using Lumina.McpServer;
using Xunit;

namespace Lumina.McpServer.Tests;

public sealed class LuminaToolsTests
{
    [Fact]
    public async Task CheckBuild_OmitsUnboundedLogsAndReportsLength()
    {
        var client = new StubClient
        {
            GetResult = JsonNode.Parse(
                """{"id":"11111111-1111-1111-1111-111111111111","status":2,"logs":"one\ntwo"}""")!
        };
        var tools = new LuminaTools(client);

        var result = await tools.CheckBuild(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CancellationToken.None);

        Assert.DoesNotContain("\"logs\"", result);
        Assert.Contains("\"logLength\": 7", result);
    }

    [Fact]
    public async Task GetBuildLogs_ReturnsOnlyRequestedTail()
    {
        var client = new StubClient
        {
            GetResult = JsonValue.Create("first\nsecond\nthird")!
        };
        var tools = new LuminaTools(client);

        var result = await tools.GetBuildLogs(
            Guid.NewGuid(), 2, CancellationToken.None);

        Assert.Equal("second\nthird", result);
    }

    [Fact]
    public async Task BuildPackage_UsesAutomaticPipelineEndpoint()
    {
        var client = new StubClient
        {
            PostResult = JsonNode.Parse("""{"id":"22222222-2222-2222-2222-222222222222"}""")!
        };
        var tools = new LuminaTools(client);
        var pipelineId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        await tools.BuildPackage(pipelineId, "test-agent", CancellationToken.None);

        Assert.Equal(
            "api/pipelines/11111111-1111-1111-1111-111111111111/trigger-auto",
            client.LastPostPath);
    }

    private sealed class StubClient : ILuminaApiClient
    {
        public JsonNode GetResult { get; init; } = new JsonObject();
        public JsonNode PostResult { get; init; } = new JsonObject();
        public string? LastPostPath { get; private set; }

        public Task<JsonNode> GetDataAsync(
            string path,
            CancellationToken cancellationToken = default)
            => Task.FromResult(GetResult.DeepClone());

        public Task<JsonNode> PostDataAsync(
            string path,
            object? body,
            CancellationToken cancellationToken = default)
        {
            LastPostPath = path;
            return Task.FromResult(PostResult.DeepClone());
        }
    }
}
