using System.Text.Json;
using Lumina.Shared.DTOs;
using Lumina.Shared.Models.Enums;
using Lumina.Shared.Security;
using Lumina.Web.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Lumina.BuildService.Controllers;

[ApiController]
[Route("api/[controller]")]
// Git providers push here without a JWT; the per-pipeline webhook-secret HMAC
// check in HandleWebhook is the real gate. YARP also marks this route
// AuthorizationPolicy: "lumina-anonymous". AllowAnonymous also exempts it from
// the FallbackPolicy so the global "require auth" default doesn't reject
// webhooks.
[AllowAnonymous]
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
    /// Hard ceiling on a webhook request body. Webhooks carry push *metadata*
    /// (refs, commit ids, author) — never source — so 1 MiB is generous and keeps
    /// an anonymous caller from OOM-ing the service with a multi-MB JSON blob.
    /// </summary>
    private const int MaxWebhookBodyBytes = 1 * 1024 * 1024; // 1 MiB

    /// <summary>
    /// Generic webhook endpoint for Git push events.
    /// Supports GitHub, GitLab, and Forgejo/Gitea webhook formats.
    /// POST /api/webhooks/{pipelineId}
    /// </summary>
    // RequestSizeLimit is enforced by Kestrel at the transport, so an oversized
    // body is rejected before it is ever buffered/parsed. The in-action cap below
    // is defense-in-depth for hosts that ignore the attribute / lying clients.
    [HttpPost("{pipelineId:guid}")]
    [RequestSizeLimit(MaxWebhookBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxWebhookBodyBytes)]
    public async Task<ActionResult<ApiResponse<BuildJobResponse?>>> HandleWebhook(Guid pipelineId)
    {
        _logger.LogInformation("Webhook received for pipeline {PipelineId}", pipelineId);

        var pipeline = await _engine.GetPipelineAsync(pipelineId);
        if (pipeline == null)
            return NotFound(new ApiResponse<BuildJobResponse?>(false, null, "Pipeline not found", null));

        // Read the RAW request body once, under a hard cap. We deliberately do NOT
        // use [FromBody] JsonElement here:
        //   • It would parse the entire document up front (before any signature
        //     check), so an unauthenticated caller could push a multi-MB payload
        //     and OOM the service.
        //   • Verifying over payload.GetRawText() materializes the whole document
        //     again AND recomputes the HMAC over a re-serialized representation
        //     that does not match the bytes the provider actually signed.
        // Reading the raw bytes lets us verify the HMAC over exactly what the
        // provider signed, and only then parse.
        var rawBody = await ReadBodyWithCapAsync(Request.Body, MaxWebhookBodyBytes, HttpContext.RequestAborted);
        if (rawBody is null)
        {
            _logger.LogWarning(
                "Webhook for pipeline {PipelineId} rejected: body exceeds {Limit} bytes",
                pipelineId, MaxWebhookBodyBytes);
            return StatusCode(413, new ApiResponse<BuildJobResponse?>(false, null, "Webhook payload too large", null));
        }

        // Fail-closed: a pipeline WITHOUT a configured webhook secret can never
        // be triggered via this endpoint. The earlier behavior skipped the check
        // when the secret was empty, which let anyone who learned a pipeline id
        // POST an arbitrary payload (including a forged repoUrl) and trigger a
        // build — chaining into the git-clone-as-root and untrusted-spec risks
        // (SEC-02 / SEC-04). Pipelines are therefore required to carry a secret
        // at creation time (see PipelineEngine.CreatePipelineAsync); this gate
        // is the defense-in-depth backstop for rows that predate that rule.
        if (string.IsNullOrEmpty(pipeline.WebhookSecret))
        {
            _logger.LogWarning(
                "Webhook for pipeline {PipelineId} rejected: no webhook secret configured (fail-closed)", pipelineId);
            return Unauthorized(new ApiResponse<BuildJobResponse?>(false, null,
                "Webhook secret not configured for this pipeline", null));
        }

        // Verify the webhook secret over the RAW bytes. This runs BEFORE the body
        // is parsed, so an invalid-signature request never reaches the JSON parser.
        if (!VerifySignature(pipeline.WebhookSecret, rawBody))
        {
            _logger.LogWarning("Webhook signature verification failed for pipeline {PipelineId}", pipelineId);
            return Unauthorized(new ApiResponse<BuildJobResponse?>(false, null, "Invalid signature", null));
        }

        // Signature verified — safe to parse now.
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            payload = doc.RootElement.Clone(); // keep alive past the using
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Webhook body for pipeline {PipelineId} is not valid JSON", pipelineId);
            return BadRequest(new ApiResponse<BuildJobResponse?>(false, null, "Invalid JSON payload", null));
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

        // Branch filtering: if pipeline has a configured branch, only trigger on matching pushes
        var targetBranch = pipeline.GitBranch ?? "main";
        if (!string.IsNullOrEmpty(branch) &&
            !string.Equals(branch, targetBranch, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Webhook branch mismatch: push to '{PushBranch}' but pipeline targets '{TargetBranch}'. Skipping.",
                branch, targetBranch);
            return Ok(new ApiResponse<BuildJobResponse?>(true, null, null,
                $"Push to '{branch}' ignored — pipeline targets '{targetBranch}'"));
        }

        // Determine spec name from pipeline or path
        var specName = !string.IsNullOrEmpty(pipeline.SpecPath)
            ? Path.GetFileName(pipeline.SpecPath)
            : $"{pipeline.Name}.spec";

        var effectiveBranch = branch ?? targetBranch;

        // Create build request — source will be cloned from git in the container
        var request = new TriggerBuildRequest(
            specName,
            string.Empty,  // Spec content will be read from cloned repo
            $"git://{repoUrl}#branch={effectiveBranch}&specPath={pipeline.SpecPath ?? specName}&commit={commit}",
            author ?? "webhook",
            commit,
            effectiveBranch,
            ExtractCommitMessage(payload),
            author
        );

        try
        {
            var job = await _engine.TriggerBuildAsync(pipelineId, request);
            var response = new BuildJobResponse(job.Id, job.PipelineId, job.Status, job.SpecName,
                job.ContainerId, job.Logs, job.CreatedAt, job.StartedAt, job.CompletedAt, job.TriggeredBy,
                job.Artifacts.Select(a => new BuildArtifactResponse(a.Id, a.FileName, a.FileSize,
                    a.HashSha256, a.HashMd5, a.PgpSignature, a.CveScanStatus)).ToList(),
                job.SourceUrl, job.CommitSha, job.Branch, job.CommitMessage, job.CommitAuthor);

            return Ok(new ApiResponse<BuildJobResponse?>(true, response, null, "Build triggered from webhook"));
        }
        catch (Exception ex)
        {
            // NotFoundException (pipeline gone) -> 404; ValidationException
            // (Sign-step key gate) -> 400; anything else -> generic 500. Never
            // surface ex.Message — it can carry DB/stack hints (SEC-022).
            return ApiResults.FromException<BuildJobResponse?>(ex, _logger, "Webhooks.TriggerBuild", pipelineId);
        }
    }

    /// <summary>
    /// Reads <paramref name="stream"/> to end into a byte array, aborting (and
    /// returning <c>null</c>) as soon as <paramref name="maxBytes"/> is exceeded.
    /// The cap is enforced on bytes actually read, so an attacker streaming a
    /// huge body is cut off mid-stream rather than after buffering it whole.
    /// </summary>
    private static async Task<byte[]?> ReadBodyWithCapAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        // If the client advertised a Content-Length, reject oversize up front
        // without reading a single byte.
        var cap = (long)maxBytes;
        if (stream.CanSeek && stream.Length > cap)
            return null;

        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 8192));
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > cap)
                return null; // exceeded cap — refuse without persisting the rest
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return ms.Length == 0 ? Array.Empty<byte>() : ms.ToArray();
    }

    private bool VerifySignature(string secret, byte[] rawBody)
    {
        // GitHub: X-Hub-Signature-256 header
        // GitLab: X-Gitlab-Token header
        // Forgejo/Gitea: X-Forgejo-Signature header
        //
        // The actual HMAC / constant-time comparison now lives in
        // WebhookSignatureVerifier (Lumina.Shared.Security) so it can be unit-
        // tested directly. This method just maps the ASP.NET header dictionary
        // onto the framework-agnostic WebhookHeaders struct.
        var headers = new WebhookHeaders(
            GitLabToken: Request.Headers["X-Gitlab-Token"].FirstOrDefault(),
            HubSignature256: Request.Headers["X-Hub-Signature-256"].FirstOrDefault(),
            ForgejoSignature: Request.Headers["X-Forgejo-Signature"].FirstOrDefault());

        return WebhookSignatureVerifier.Verify(secret, rawBody, headers);
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

    private string? ExtractCommitMessage(JsonElement payload)
    {
        try
        {
            // GitHub / Forgejo / Gitea format
            if (payload.TryGetProperty("head_commit", out var headCommit))
            {
                if (headCommit.TryGetProperty("message", out var msg))
                    return msg.GetString()?.Split('\n').FirstOrDefault(); // First line only
            }

            // GitLab format
            if (payload.TryGetProperty("commits", out var commits) && commits.GetArrayLength() > 0)
            {
                var lastCommit = commits[0];
                if (lastCommit.TryGetProperty("message", out var msg))
                    return msg.GetString()?.Split('\n').FirstOrDefault();
            }
        }
        catch { /* best effort */ }

        return null;
    }
}
