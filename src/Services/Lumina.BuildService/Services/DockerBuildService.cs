using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

public class DockerBuildService
{
    private readonly DockerClient _docker;
    private readonly BuildDbContext _db;
    private readonly ILogger<DockerBuildService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBus _bus;

    public DockerBuildService(
        BuildDbContext db,
        ILogger<DockerBuildService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IBus bus)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _bus = bus;
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
            // Ensure the build image exists locally — try to pull if missing
            await EnsureImageExistsAsync(imageName);
            job.Status = BuildStatus.Building;
            job.StartedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();

            // Container-internal path for artifact scanning (matches docker-compose volume mount)
            var artifactDir = $"/app/builds/{job.Id}";
            // Host-side path for RPM container bind mounts (Docker API resolves on host)
            var hostArtifactDir = $"/opt/lumina/builds/{job.Id}";
            Directory.CreateDirectory(artifactDir);

            var envVars = new List<string>
            {
                $"SPEC_NAME={job.SpecName}",
                $"ARTIFACTS_DIR=/artifacts",
                $"BUILD_JOB_ID={job.Id}",
                "AUTO_DOWNLOAD=true"
            };

            // Write spec content to a file and mount it — avoids "argument list too long" for large specs
            // Uses /opt/lumina/sources which is mounted from the host, so the ephemeral build container can access it
            string? hostSpecDir = null;
            if (!string.IsNullOrEmpty(specContent))
            {
                hostSpecDir = $"/opt/lumina/sources/_specs/{job.Id}";
                Directory.CreateDirectory(hostSpecDir);
                var specFilePath = Path.Combine(hostSpecDir, job.SpecName);
                await File.WriteAllTextAsync(specFilePath, specContent);
                _logger.LogInformation("Spec file written to {SpecFilePath} ({Size} bytes)", specFilePath, specContent.Length);
            }

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
                $"{hostArtifactDir}:/artifacts:z"
            };

            // Mount spec file into container at /specs/
            if (!string.IsNullOrEmpty(hostSpecDir) && Directory.Exists(hostSpecDir))
            {
                binds.Add($"{hostSpecDir}:/specs:z");
                _logger.LogInformation("Mounting spec file from {SpecDir} to /specs", hostSpecDir);
            }

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
                    Memory = 2L * 1024 * 1024 * 1024, // 2GB limit
                    NetworkMode = "host"
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

                // Scan for built RPM artifacts
                try
                {
                    await ScanArtifactsAsync(db, dbJob);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to scan artifacts for job {JobId}", job.Id);
                }
            }
            else
            {
                dbJob.Status = BuildStatus.Failed;
                _logger.LogWarning("Build job {JobId} failed with exit code {ExitCode}", job.Id, waitResult.StatusCode);
            }

            dbJob.CompletedAt = DateTime.UtcNow;
            db.BuildJobs.Update(dbJob);
            await db.SaveChangesAsync();

            // Trigger CVE scan, hash storage, and PGP signing via MassTransit for all registered artifacts
            if (waitResult.StatusCode == 0)
            {
                _ = PublishPostBuildEventsAsync(dbJob.Id);
            }

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

    /// <summary>
    /// Scan the artifacts directory for built .rpm files and create BuildArtifact records.
    /// </summary>
    private async Task ScanArtifactsAsync(BuildDbContext db, BuildJob job)
    {
        var artifactDir = $"/app/builds/{job.Id}";
        if (!Directory.Exists(artifactDir))
        {
            _logger.LogWarning("Artifact directory {Dir} does not exist for job {JobId}", artifactDir, job.Id);
            return;
        }

        var rpmFiles = Directory.GetFiles(artifactDir, "*.rpm", SearchOption.TopDirectoryOnly);
        _logger.LogInformation("Found {Count} RPM artifacts for job {JobId}", rpmFiles.Length, job.Id);

        foreach (var rpmPath in rpmFiles)
        {
            var fileName = Path.GetFileName(rpmPath);
            var fileInfo = new FileInfo(rpmPath);

            // Compute SHA256 hash
            string hashSha256;
            using (var stream = File.OpenRead(rpmPath))
            {
                var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
                hashSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Compute MD5 hash
            string hashMd5;
            using (var stream = File.OpenRead(rpmPath))
            {
                var hashBytes = await System.Security.Cryptography.MD5.HashDataAsync(stream);
                hashMd5 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            var artifact = new BuildArtifact
            {
                Id = Guid.NewGuid(),
                BuildJobId = job.Id,
                FileName = fileName,
                FilePath = rpmPath,
                FileSize = fileInfo.Length,
                HashSha256 = hashSha256,
                HashMd5 = hashMd5,
                StoragePath = rpmPath,
                CveScanStatus = ScanStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            db.BuildArtifacts.Add(artifact);
            _logger.LogInformation("Registered artifact {FileName} ({Size} bytes, SHA256: {Hash}) for job {JobId}",
                fileName, fileInfo.Length, hashSha256[..16] + "...", job.Id);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Publish post-build events via MassTransit: CVE scan requests and hash storage.
    /// PGP signing is now handled by CveScanCompletedConsumer after CVE scan passes.
    /// </summary>
    private async Task PublishPostBuildEventsAsync(Guid jobId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
            var artifacts = await db.BuildArtifacts.Where(a => a.BuildJobId == jobId).ToListAsync();

            foreach (var artifact in artifacts)
            {
                // Publish CVE scan request
                // After scan completes, CveScanCompletedConsumer will request PGP signing if no critical/high vulns
                await _bus.Publish(new CveScanRequested(
                    artifact.Id,
                    artifact.FilePath,
                    artifact.FileName,
                    "Trivy",
                    DateTime.UtcNow
                ));
                _logger.LogInformation("Published CveScanRequested for artifact {ArtifactId}", artifact.Id);

                // Publish hash storage request
                await _bus.Publish(new HashStoreRequested(
                    artifact.Id,
                    artifact.FileName,
                    artifact.HashSha256,
                    artifact.HashMd5,
                    artifact.FileSize,
                    DateTime.UtcNow
                ));
                _logger.LogInformation("Published HashStoreRequested for artifact {ArtifactId}", artifact.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish post-build events for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Ensure the specified Docker image exists locally. If not found, attempt to pull it.
    /// This prevents build failures when images haven't been pre-built on a new device.
    /// </summary>
    private async Task EnsureImageExistsAsync(string imageName)
    {
        try
        {
            // Check if image exists locally
            var images = await _docker.Images.ListImagesAsync(new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                }
            });

            if (images.Count > 0)
            {
                _logger.LogDebug("Build image {Image} found locally", imageName);
                return;
            }

            _logger.LogWarning("Build image {Image} not found locally, attempting to pull...", imageName);

            // Try to pull the image from a registry
            try
            {
                await _docker.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = imageName },
                    null,
                    new Progress<JSONMessage>(msg =>
                    {
                        if (!string.IsNullOrEmpty(msg.Status))
                            _logger.LogDebug("Pull {Image}: {Status}", imageName, msg.Status);
                    }));
                _logger.LogInformation("Successfully pulled build image {Image}", imageName);
            }
            catch (Exception pullEx)
            {
                _logger.LogError(pullEx, "Failed to pull build image {Image}. " +
                    "Build it manually: docker compose -f deploy/docker-compose.yml build rpm-build-image", imageName);
                throw new InvalidOperationException(
                    $"Build image '{imageName}' is not available locally and could not be pulled. " +
                    "Build it first with: docker compose -f deploy/docker-compose.yml build rpm-build-image", pullEx);
            }
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw our own exception
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify build image {Image} existence, proceeding anyway", imageName);
        }
    }

    public async Task<bool> CancelBuildAsync(Guid jobId)
    {
        var job = await _db.BuildJobs.FindAsync(jobId);
        if (job == null) return false;
        if (job.Status != BuildStatus.Queued && job.Status != BuildStatus.Building) return false;

        try
        {
            // Stop Docker container if one exists
            if (!string.IsNullOrEmpty(job.ContainerId))
            {
                try
                {
                    await _docker.Containers.StopContainerAsync(job.ContainerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
                    _logger.LogInformation("Stopped container {ContainerId} for cancelled build {BuildId}", job.ContainerId, jobId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop container {ContainerId} for build {BuildId}", job.ContainerId, jobId);
                }

                // Remove the container
                try
                {
                    await _docker.Containers.RemoveContainerAsync(job.ContainerId, new ContainerRemoveParameters { Force = true });
                }
                catch { /* best effort */ }
            }

            var previousStatus = job.Status.ToString();
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Build {BuildId} cancelled (was {PreviousStatus})", jobId, previousStatus);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel build {BuildId}", jobId);
            return false;
        }
    }
}