using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhooksController : ControllerBase
{
    private readonly Services.PipelineEngine _engine;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(Services.PipelineEngine engine, ILogger<WebhooksController> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>
    /// Generic webhook endpoint for Git push events.
    /// Supports GitHub, GitLab, and Forgejo/Gitea webhook formats.
    /// POST /api/webhooks/{pipelineId}
    /// </summary>
    [HttpPost("{pipelineId:guid}")]
    public async Task<ActionResult<ApiResponse<BuildJobResponse?>>> HandleWebhook(
        Guid pipelineId,
        [FromBody] JsonElement payload)
    {
        _logger.LogInformation("Webhook received for pipeline {PipelineId}", pipelineId);

        var pipeline = await _engine.GetPipelineAsync(pipelineId);
        if (pipeline == null)
            return NotFound(new ApiResponse<BuildJobResponse?>(false, null, "Pipeline not found", null));

        // Verify webhook secret if configured
        if (!string.IsNullOrEmpty(pipeline.WebhookSecret))
        {
            if (!VerifySignature(pipeline.WebhookSecret, payload))
            {
                _logger.LogWarning("Webhook signature verification failed for pipeline {PipelineId}", pipelineId);
                return Unauthorized(new ApiResponse<BuildJobResponse?>(false, null, "Invalid signature", null));
            }
        }

        // Extract info from webhook payload
        var (repoUrl, branch, commit, author) = ExtractPushInfo(payload);

        if (string.IsNullOrEmpty(repoUrl))
        {
            // Use pipeline's configured repo URL
            repoUrl = pipeline.GitRepoUrl;
        }

        if (string.IsNullOrEmpty(branch))
        {
            branch = pipeline.GitBranch ?? "main";
        }

        _logger.LogInformation(
            "Webhook push: repo={RepoUrl}, branch={Branch}, commit={Commit}, author={Author}",
            repoUrl, branch, commit, author);

        // Determine spec name from pipeline or path
        var specName = !string.IsNullOrEmpty(pipeline.SpecPath)
            ? Path.GetFileName(pipeline.SpecPath)
            : $"{pipeline.Name}.spec";

        // Create build request — source will be cloned from git in the container
        var request = new TriggerBuildRequest(
            specName,
            string.Empty,  // Spec content will be read from cloned repo
            $"git://{repoUrl}#branch={branch}&specPath={pipeline.SpecPath ?? specName}&commit={commit}",
            author ?? "webhook"
        );

        try
        {
            var job = await _engine.TriggerBuildAsync(pipelineId, request);
            var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName,
                job.ContainerId, job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy,
                job.Artifacts.Select(a => new BuildArtifactResponse(a.Id, a.FileName, a.FileSize,
                    a.HashSha256, a.HashMd5, a.PgpSignature, a.CveScanStatus)).ToList());

            return Ok(new ApiResponse<BuildJobResponse?>(true, response, null, "Build triggered from webhook"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger build from webhook for pipeline {PipelineId}", pipelineId);
            return StatusCode(500, new ApiResponse<BuildJobResponse?>(false, null, ex.Message, null));
        }
    }

    private bool VerifySignature(string secret, JsonElement payload)
    {
        // GitHub: X-Hub-Signature-256 header
        // GitLab: X-Gitlab-Token header
        // Forgejo/Gitea: X-Forgejo-Signature header

        // Check GitLab token (simple token comparison)
        if (Request.Headers.TryGetValue("X-Gitlab-Token", out var gitlabToken))
            return gitlabToken == secret;

        // Check GitHub/Forgejo HMAC signature
        var signatureHeader = Request.Headers["X-Hub-Signature-256"].FirstOrDefault()
            ?? Request.Headers["X-Forgejo-Signature"].FirstOrDefault();

        if (signatureHeader != null)
        {
            var payloadBytes = Encoding.UTF8.GetBytes(payload.GetRawText());
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(payloadBytes);
            var computedSig = $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
            return computedSig == signatureHeader.ToLowerInvariant();
        }

        // If no signature headers present, reject
        return false;
    }

    private (string? repoUrl, string? branch, string? commit, string? author) ExtractPushInfo(JsonElement payload)
    {
        string? repoUrl = null;
        string? branch = null;
        string? commit = null;
        string? author = null;

        try
        {
            // GitHub format
            if (payload.TryGetProperty("repository", out var repo))
            {
                repoUrl = repo.TryGetProperty("clone_url", out var cloneUrl) ? cloneUrl.GetString() : null;
            }
            if (payload.TryGetProperty("ref", out var @ref))
            {
                var refStr = @ref.GetString() ?? "";
                branch = refStr.StartsWith("refs/heads/") ? refStr["refs/heads/".Length..] : refStr;
            }
            if (payload.TryGetProperty("head_commit", out var headCommit))
            {
                commit = headCommit.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (headCommit.TryGetProperty("author", out var commitAuthor))
                    author = commitAuthor.TryGetProperty("username", out var username) ? username.GetString() : null;
            }

            // GitLab format
            if (payload.TryGetProperty("project", out var project))
            {
                repoUrl ??= project.TryGetProperty("git_http_url", out var gitHttpUrl) ? gitHttpUrl.GetString() : null;
            }

            // Forgejo/Gitea format (same as GitHub mostly)
            if (repoUrl == null && payload.TryGetProperty("repository", out var repo2))
            {
                repoUrl = repo2.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract push info from webhook payload");
        }

        return (repoUrl, branch, commit, author);
    }
}