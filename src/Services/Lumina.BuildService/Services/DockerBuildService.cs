using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lumina.BuildService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public class DockerBuildService
{
    private readonly DockerClient _docker;
    private readonly BuildDbContext _db;
    private readonly ILogger<DockerBuildService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public DockerBuildService(BuildDbContext db, ILogger<DockerBuildService> logger, IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        var dockerPath = config["Docker:SocketPath"] ?? "/var/run/docker.sock";
        var dockerUrl = dockerPath.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            ? dockerPath
            : $"unix://{dockerPath}";
        _docker = new DockerClientConfiguration(new Uri(dockerUrl)).CreateClient();
    }

    public async Task<BuildJob> StartBuildAsync(BuildJob job, string? specContent, string? sourceUrl, string? buildImage = null, string? gitUsername = null, string? gitToken = null, string? sourceDir = null)
    {
        var imageName = !string.IsNullOrWhiteSpace(buildImage) ? buildImage : "lumina-rpm-build:latest";
        _logger.LogInformation("Starting Docker build for job {JobId} ({SpecName}) with image {Image}", job.Id, job.SpecName, imageName);

        try
        {
            job.Status = BuildStatus.Building;
            job.StartedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();

            var artifactDir = $"/app/builds/{job.Id}";
            var envVars = new List<string>
            {
                $"SPEC_NAME={job.SpecName}",
                $"ARTIFACTS_DIR=/artifacts",
                $"BUILD_JOB_ID={job.Id}",
                "AUTO_DOWNLOAD=true"
            };

            if (!string.IsNullOrEmpty(specContent))
                envVars.Add($"SPEC_CONTENT={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(specContent))}");

            if (!string.IsNullOrEmpty(sourceUrl))
                envVars.Add($"SOURCE_URL={sourceUrl}");

            // If pre-fetched source directory is provided, mount it into the container
            if (!string.IsNullOrEmpty(sourceDir))
                envVars.Add($"SOURCE_DIR=/sources");

            // Pass git credentials for private repositories
            if (!string.IsNullOrEmpty(gitUsername))
                envVars.Add($"GIT_USERNAME={gitUsername}");

            if (!string.IsNullOrEmpty(gitToken))
                envVars.Add($"GIT_TOKEN={gitToken}");

            if (!string.IsNullOrEmpty(job.CommitSha))
                envVars.Add($"COMMIT_SHA={job.CommitSha}");

            if (!string.IsNullOrEmpty(job.Branch))
                envVars.Add($"BRANCH={job.Branch}");

            var binds = new List<string>
            {
                $"{artifactDir}:/artifacts:z"
            };

            // Mount pre-fetched sources if available
            if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
            {
                binds.Add($"{sourceDir}:/sources:z");
                _logger.LogInformation("Mounting pre-fetched sources from {SourceDir}", sourceDir);
            }

            var createParams = new CreateContainerParameters
            {
                Image = imageName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    Binds = binds,
                    Memory = 2L * 1024 * 1024 * 1024 // 2GB limit
                },
                Name = $"lumina-build-{job.Id:N}",
                Labels = new Dictionary<string, string>
                {
                    { "lumina.build-job-id", job.Id.ToString() },
                    { "lumina.pipeline-id", job.PipelineId.ToString() }
                }
            };

            var container = await _docker.Containers.CreateContainerAsync(createParams);
            job.ContainerId = container.ID;

            await _docker.Containers.StartContainerAsync(container.ID, new ContainerStartParameters());

            _logger.LogInformation("Build container {ContainerId} started for job {JobId}", container.ID, job.Id);

            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();

            _ = MonitorBuildAsync(job, container.ID);

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start build for job {JobId}", job.Id);
            job.Status = BuildStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();
            throw;
        }
    }

    private async Task MonitorBuildAsync(BuildJob job, string containerId)
    {
        try
        {
            var waitResult = await _docker.Containers.WaitContainerAsync(containerId);

            string logs = "";
            try
            {
                var logsStream = await _docker.Containers.GetContainerLogsAsync(containerId, true, new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = false
                });

                var logsBuilder = new System.Text.StringBuilder();
                var buffer = new byte[4096];
                while (true)
                {
                    var readResult = await logsStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    if (readResult.EOF || readResult.Count == 0) break;
                    logsBuilder.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, readResult.Count));
                }
                logs = logsBuilder.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read logs for container {ContainerId}", containerId);
            }

            // Use a fresh scope for DB access (original context may be disposed)
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();

            var dbJob = await db.BuildJobs.FindAsync(job.Id);
            if (dbJob == null)
            {
                _logger.LogError("Build job {JobId} not found in database", job.Id);
                return;
            }

            dbJob.Logs = logs.Replace("\0", "");
            dbJob.ContainerId = containerId;

            if (waitResult.StatusCode == 0)
            {
                dbJob.Status = BuildStatus.Success;
                _logger.LogInformation("Build job {JobId} completed successfully", job.Id);
            }
            else
            {
                dbJob.Status = BuildStatus.Failed;
                _logger.LogWarning("Build job {JobId} failed with exit code {ExitCode}", job.Id, waitResult.StatusCode);
            }

            dbJob.CompletedAt = DateTime.UtcNow;
            db.BuildJobs.Update(dbJob);
            await db.SaveChangesAsync();

            // Clean up container
            try
            {
                await _docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring build job {JobId}", job.Id);

            // Try to mark as failed with a fresh scope
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
                var dbJob = await db.BuildJobs.FindAsync(job.Id);
                if (dbJob != null)
                {
                    dbJob.Status = BuildStatus.Failed;
                    dbJob.CompletedAt = DateTime.UtcNow;
                    db.BuildJobs.Update(dbJob);
                    await db.SaveChangesAsync();
                }
            }
            catch { /* best effort */ }
        }
    }

    public async Task<bool> CancelBuildAsync(Guid jobId)
    {
        var job = await _db.BuildJobs.FindAsync(jobId);
        if (job == null || job.ContainerId == null) return false;

        try
        {
            await _docker.Containers.StopContainerAsync(job.ContainerId, new ContainerStopParameters());
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel build {JobId}", jobId);
            return false;
        }
    }
}