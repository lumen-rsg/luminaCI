using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Lumina.McpServer;

[McpServerToolType]
public sealed class LuminaTools(ILuminaApiClient client)
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true
    };

    [McpServerTool(
        Name = "lumina_list_packages",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("List configured Lumina package sources and their latest fetch status.")]
    public async Task<string> ListPackages(CancellationToken cancellationToken)
        => Format(await client.GetDataAsync("api/sources", cancellationToken));

    [McpServerTool(
        Name = "lumina_get_package",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Get one configured package source, immutable revision, checksum, and fetch status.")]
    public async Task<string> GetPackage(
        [Description("Package source slug.")] string packageName,
        CancellationToken cancellationToken)
        => Format(await client.GetDataAsync(
            $"api/sources/{Segment(packageName)}", cancellationToken));

    [McpServerTool(
        Name = "lumina_fetch_package",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Queue source fetching for a configured package. This changes Lumina state but does not start a build.")]
    public async Task<string> FetchPackage(
        [Description("Package source slug.")] string packageName,
        [Description("Maximum fetch attempts, from 1 through 10.")] int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        maxRetries = Math.Clamp(maxRetries, 1, 10);
        return Format(await client.PostDataAsync(
            $"api/sources/{Segment(packageName)}/fetch",
            new { maxRetries },
            cancellationToken));
    }

    [McpServerTool(
        Name = "lumina_list_pipelines",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("List configured build pipelines. Use a pipeline id with lumina_build_package.")]
    public async Task<string> ListPipelines(
        [Description("Optional pipeline-name search text.")] string? search = null,
        [Description("Maximum results, from 1 through 100.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var path = $"api/pipelines?page=1&pageSize={limit}";
        if (!string.IsNullOrWhiteSpace(search))
            path += $"&search={Uri.EscapeDataString(search)}";
        return Format(await client.GetDataAsync(path, cancellationToken));
    }

    [McpServerTool(
        Name = "lumina_build_package",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Start an automatic package build from a configured pipeline's Git source. Returns the queued build id.")]
    public async Task<string> BuildPackage(
        [Description("Configured pipeline UUID.")] Guid pipelineId,
        [Description("Short actor label recorded on the build.")] string triggeredBy = "mcp-agent",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(triggeredBy) || triggeredBy.Length > 100)
            throw new ArgumentException(
                "triggeredBy must contain between 1 and 100 characters.",
                nameof(triggeredBy));

        return Format(await client.PostDataAsync(
            $"api/pipelines/{pipelineId:D}/trigger-auto",
            new { triggeredBy },
            cancellationToken));
    }

    [McpServerTool(
        Name = "lumina_list_builds",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("List recent package builds, optionally filtered by status.")]
    public async Task<string> ListBuilds(
        [Description("Optional status: Queued, Building, Success, Failed, or Cancelled.")] string? status = null,
        [Description("Maximum results, from 1 through 100.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var path = $"api/builds?page=1&pageSize={limit}";
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = NormalizeBuildStatus(status);
            path += $"&status={normalized}";
        }

        return Format(await client.GetDataAsync(path, cancellationToken));
    }

    [McpServerTool(
        Name = "lumina_check_build",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Check a build's status, pipeline steps, RPM artifacts, hashes, scan/signing state, and publication state. Logs are omitted; use lumina_get_build_logs.")]
    public async Task<string> CheckBuild(
        [Description("Build UUID returned by lumina_build_package.")] Guid buildId,
        CancellationToken cancellationToken)
    {
        var data = await client.GetDataAsync(
            $"api/builds/{buildId:D}", cancellationToken);
        if (data is JsonObject build)
        {
            var logLength = build["logs"]?.GetValue<string>()?.Length ?? 0;
            build.Remove("logs");
            build["logLength"] = logLength;
        }
        return Format(data);
    }

    [McpServerTool(
        Name = "lumina_get_build_logs",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Get the tail of a package build log. Output is bounded to protect the agent context.")]
    public async Task<string> GetBuildLogs(
        [Description("Build UUID.")] Guid buildId,
        [Description("Number of trailing lines, from 1 through 1000.")] int tailLines = 200,
        CancellationToken cancellationToken = default)
    {
        tailLines = Math.Clamp(tailLines, 1, 1000);
        var data = await client.GetDataAsync(
            $"api/builds/{buildId:D}/logs", cancellationToken);
        var logs = data.GetValue<string>();
        var lines = logs.Split('\n');
        return string.Join('\n', lines.TakeLast(tailLines));
    }

    private static string NormalizeBuildStatus(string status)
    {
        var allowed = new[] { "Queued", "Building", "Success", "Failed", "Cancelled" };
        return allowed.FirstOrDefault(
                   item => item.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException(
                   $"Unknown build status '{status}'.", nameof(status));
    }

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Package name is required.", nameof(value));
        return Uri.EscapeDataString(value.Trim());
    }

    private static string Format(JsonNode data)
        => data.ToJsonString(OutputOptions);
}
