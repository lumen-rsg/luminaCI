using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lumina.BuildService.Data;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Manages Docker-based builds with real-time log streaming.
/// Logs are streamed from Docker containers as they arrive and made available
/// to SSE subscribers via Channel-based pub/sub.
/// </summary>
public class DockerBuildService
{
    private readonly DockerClient _docker;
    private readonly BuildDbContext _db;
    private readonly ILogger<DockerBuildService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBus _bus;

    /// <summary>
    /// Docker daemon endpoint (unix socket or HTTP proxy URL). When fronted by a
    /// docker-socket-proxy, only the whitelisted endpoints the proxy exposes are
    /// reachable from this service.
    /// </summary>
    private readonly string _dockerUrl;

    /// <summary>Dedicated, isolated bridge network for untrusted build containers. Build containers are attached here instead of the host network so they cannot reach Postgres/RabbitMQ/MinIO/Trivy directly.</summary>
    private readonly string _buildNetwork;

    /// <summary>Memory cap per build container, in bytes (default 2 GiB).</summary>
    private readonly long _buildMemoryBytes;

    /// <summary>PID limit per build container (default 512) to blunt fork bombs.</summary>
    private readonly long _buildPidsLimit;

    /// <summary>CPU quota per build container in microseconds/period (default 150000 = 1.5 CPUs).</summary>
    private readonly long _buildCpuQuota;

    /// <summary>Full accumulated logs per build job (in-memory buffer).</summary>
    private static readonly ConcurrentDictionary<Guid, string> _logBuffers = new();

    /// <summary>Subscribers per build job that receive new log lines in real-time.</summary>
    private static readonly ConcurrentDictionary<Guid, List<Channel<string>>> _subscribers = new();

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

        // The Docker endpoint may be a raw unix socket (unix:///var/run/docker.sock),
        // a host:port, or an http(s):// URL fronting a docker-socket-proxy. The proxy
        // is the recommended deployment: it whitelists only the API calls build-service
        // actually needs, so a compromised build-service cannot spawn arbitrary images
        // or read other containers.
        var dockerEndpoint = config["Docker:SocketPath"] ?? "/var/run/docker.sock";
        _dockerUrl = dockerEndpoint.Contains("://", StringComparison.Ordinal)
            ? dockerEndpoint
            : $"unix://{dockerEndpoint}";
        _docker = new DockerClientConfiguration(new Uri(_dockerUrl)).CreateClient();

        // Build-container isolation knobs. Defaults assume untrusted specs that may
        // run arbitrary shell inside the rpmbuild container, so they're deliberately
        // tight and can only be loosened via configuration.
        _buildNetwork = config["Docker:BuildNetwork"] ?? "lumina-buildnet";
        _buildMemoryBytes = ParseLongConfig(config, "Docker:BuildMemoryBytes", 2L * 1024 * 1024 * 1024);
        _buildPidsLimit = ParseLongConfig(config, "Docker:BuildPidsLimit", 512);
        _buildCpuQuota = ParseLongConfig(config, "Docker:BuildCpuQuota", 150_000); // 1.5 CPUs (period 100000 µs)
    }

    /// <summary>Parse a long configuration value, returning <paramref name="defaultValue"/> when missing or invalid.</summary>
    private static long ParseLongConfig(IConfiguration config, string key, long defaultValue)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        return long.TryParse(raw.Trim(), out var v) ? v : defaultValue;
    }

    /// <summary>
    /// Get current accumulated logs for a build job (from in-memory buffer or database).
    /// </summary>
    public async Task<string> GetLogsAsync(Guid jobId)
    {
        if (_logBuffers.TryGetValue(jobId, out var logs))
            return logs;

        // Fallback to database
        var job = await _db.BuildJobs.FindAsync(jobId);
        return job?.Logs ?? "";
    }

    /// <summary>
    /// Subscribe to real-time log updates for a build job.
    /// Returns a Channel reader that receives new log lines as they arrive.
    /// </summary>
    public (string existingLogs, ChannelReader<string> reader) SubscribeToLogs(Guid jobId)
    {
        var existingLogs = _logBuffers.TryGetValue(jobId, out var buf) ? buf : "";
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers.AddOrUpdate(
            jobId,
            [channel],
            (_, list) => { lock (list) { list.Add(channel); } return list; }
        );

        return (existingLogs, channel.Reader);
    }

    /// <summary>
    /// Unsubscribe from log updates. Must be called when SSE connection closes.
    /// </summary>
    public void UnsubscribeFromLogs(Guid jobId, ChannelReader<string> reader)
    {
        if (_subscribers.TryGetValue(jobId, out var list))
        {
            lock (list)
            {
                list.RemoveAll(ch => ch.Reader == reader);
            }
            if (list.Count == 0)
                _subscribers.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Check if a build is currently being monitored (streaming logs).
    /// </summary>
    public static bool IsStreaming(Guid jobId) => _logBuffers.ContainsKey(jobId);

    private void PublishLogLine(Guid jobId, string line)
    {
        if (!_subscribers.TryGetValue(jobId, out var list)) return;
        lock (list)
        {
            foreach (var channel in list)
            {
                channel.Writer.TryWrite(line);
            }
        }
    }

    private void CleanupLogStreaming(Guid jobId)
    {
        _logBuffers.TryRemove(jobId, out _);
        if (_subscribers.TryRemove(jobId, out var list))
        {
            foreach (var channel in list)
                channel.Writer.TryComplete();
        }
    }

    public async Task<BuildJob> StartBuildAsync(BuildJob job, string? specContent, string? sourceUrl, string? buildImage = null, string? gitUsername = null, string? gitToken = null, string? sourceDir = null, string? extraSourcesPipelineDir = null)
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

            // Mount extra uploaded sources (pipeline-level only — uploaded via the
            // Extra Sources UI into /opt/lumina/extra-sources/pipelines/{pipelineId}/).
            var hostExtraDir = $"/opt/lumina/extra-sources/_builds/{job.Id}";
            if (!string.IsNullOrEmpty(extraSourcesPipelineDir) && Directory.Exists(extraSourcesPipelineDir))
            {
                Directory.CreateDirectory(hostExtraDir);

                // Copy all pipeline extra sources preserving subdirectory structure
                foreach (var dir in Directory.GetDirectories(extraSourcesPipelineDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(extraSourcesPipelineDir, dir);
                    Directory.CreateDirectory(Path.Combine(hostExtraDir, relPath));
                }
                foreach (var file in Directory.GetFiles(extraSourcesPipelineDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(extraSourcesPipelineDir, file);
                    var dest = Path.Combine(hostExtraDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                }
                _logger.LogInformation("Copied pipeline extra sources from {Dir}", extraSourcesPipelineDir);

                binds.Add($"{hostExtraDir}:/extra-sources:z");
                _logger.LogInformation("Mounted extra sources at /extra-sources for job {JobId}", job.Id);
            }

            // Build-container hardening. These containers run attacker-influenced
            // .spec files (arbitrary shell via %prep/%build/%install, dnf builddep),
            // so they must be treated as untrusted:
            //   * Isolated bridge network (not host) — no direct path to Postgres /
            //     RabbitMQ / MinIO / Trivy on lumina-network.
            //   * All capabilities dropped; only the minimal set rpmbuild/dnf needs
            //     is re-added.
            //   * Memory capped with swap pinned equal to memory (no overcommit).
            //   * PID limit to blunt fork bombs; OOM-killer enabled (a runaway build
            //     dies rather than wedging the host).
            //   * no-new-privileges to block setuid escalation; init shim for proper
            //     signal/reaping of build subprocesses.
            // User-namespace remap is expected to be enabled on the daemon itself
            // (deploy/docker/daemon.json), so root inside the container maps to a
            // non-privileged uid on the host.
            var createParams = new CreateContainerParameters
            {
                Image = imageName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    Binds = binds,
                    Memory = _buildMemoryBytes,
                    MemorySwap = _buildMemoryBytes, // disallow swap growth beyond the memory cap
                    PidsLimit = _buildPidsLimit,
                    CPUQuota = _buildCpuQuota,
                    CPUPeriod = 100_000, // standard 100ms period; CPUQuota then expresses fractional CPUs
                    CapDrop = new List<string> { "ALL" },
                    CapAdd = new List<string> { "CHOWN", "FOWNER", "SETGID", "SETUID", "DAC_OVERRIDE" },
                    SecurityOpt = new List<string> { "no-new-privileges:true" },
                    OomKillDisable = false,
                    Init = true,
                    NetworkMode = _buildNetwork // isolated bridge — NOT the host network
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
        var logBuilder = new System.Text.StringBuilder();
        var logLock = new object();

        // Initialize the in-memory log buffer for SSE subscribers
        _logBuffers[job.Id] = "";

        try
        {
            // Stream logs in parallel with waiting for container completion
            var waitTask = _docker.Containers.WaitContainerAsync(containerId);
            var cts = new CancellationTokenSource();

            // Background task: stream logs from Docker container in real-time
            var streamTask = Task.Run(async () =>
            {
                try
                {
                    var logsStream = await _docker.Containers.GetContainerLogsAsync(containerId, true, new ContainerLogsParameters
                    {
                        ShowStdout = true,
                        ShowStderr = true,
                        Follow = true,
                        Timestamps = false
                    });

                    var buffer = new byte[8192];
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var readResult = await logsStream.ReadOutputAsync(buffer, 0, buffer.Length, cts.Token);
                        if (readResult.EOF || readResult.Count == 0) break;

                        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, readResult.Count)
                            .Replace("\0", "");

                        if (string.IsNullOrEmpty(text)) continue;

                        // Append to log builder and update buffer
                        List<string> newLines;
                        lock (logLock)
                        {
                            logBuilder.Append(text);
                            var fullText = logBuilder.ToString();

                            // Update the in-memory buffer atomically
                            _logBuffers[job.Id] = fullText;

                            // Extract only the newly added lines for SSE subscribers
                            newLines = text.Split('\n').ToList();
                        }

                        // Publish each new line to SSE subscribers
                        foreach (var line in newLines)
                        {
                            var trimmed = line.TrimEnd('\r');
                            if (!string.IsNullOrEmpty(trimmed))
                            {
                                PublishLogLine(job.Id, trimmed);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when build completes
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Log streaming ended for container {ContainerId}", containerId);
                }
            }, cts.Token);

            // Also run periodic DB flush for log persistence (every 5 seconds)
            var dbFlushCts = new CancellationTokenSource();
            var dbFlushTask = Task.Run(async () =>
            {
                try
                {
                    while (!dbFlushCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), dbFlushCts.Token);
                        try
                        {
                            string currentLogs;
                            lock (logLock)
                            {
                                currentLogs = logBuilder.ToString();
                            }

                            if (!string.IsNullOrEmpty(currentLogs))
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
                                var dbJob = await db.BuildJobs.FindAsync(job.Id);
                                if (dbJob != null)
                                {
                                    dbJob.Logs = currentLogs;
                                    db.BuildJobs.Update(dbJob);
                                    await db.SaveChangesAsync();
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Periodic log flush failed for job {JobId}", job.Id);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, dbFlushCts.Token);

            // Wait for the container to finish
            var waitResult = await waitTask;

            // Stop streaming — container is done
            cts.Cancel();
            dbFlushCts.Cancel();

            try { await streamTask; } catch { /* ignore */ }
            try { await dbFlushTask; } catch { /* ignore */ }

            // Final log read (in case streaming missed the tail)
            try
            {
                var finalStream = await _docker.Containers.GetContainerLogsAsync(containerId, true, new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Follow = false
                });

                var finalBuilder = new System.Text.StringBuilder();
                var buffer = new byte[8192];
                while (true)
                {
                    var readResult = await finalStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    if (readResult.EOF || readResult.Count == 0) break;
                    finalBuilder.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, readResult.Count));
                }

                var finalLogs = finalBuilder.ToString().Replace("\0", "");
                if (finalLogs.Length > logBuilder.Length)
                {
                    // Update with more complete logs from final read
                    lock (logLock)
                    {
                        logBuilder.Clear();
                        logBuilder.Append(finalLogs);
                    }
                    _logBuffers[job.Id] = finalLogs;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Final log read failed for container {ContainerId}, using streamed logs", containerId);
            }

            // Get the final logs
            string logs;
            lock (logLock)
            {
                logs = logBuilder.ToString();
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

            dbJob.Logs = logs;
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

                // Save failed build log to file for diagnostics
                SaveFailedBuildLog(job.Id, job.SpecName, logs);
            }

            dbJob.CompletedAt = DateTime.UtcNow;
            db.BuildJobs.Update(dbJob);
            await db.SaveChangesAsync();

            // Notify SSE subscribers that build is complete
            var buildResult = waitResult.StatusCode == 0 ? "SUCCESS" : "FAILED";
            PublishLogLine(job.Id, $"[BUILD {buildResult}]");

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

            // Notify subscribers about the error
            PublishLogLine(job.Id, "[BUILD ERROR - monitoring failed]");
            try { _logBuffers[job.Id] = logBuilder.ToString(); } catch { }

            // Try to mark as failed with a fresh scope
            try
            {
                string errorLogs = logBuilder.ToString();
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
                var dbJob = await db.BuildJobs.FindAsync(job.Id);
                if (dbJob != null)
                {
                    dbJob.Logs = errorLogs;
                    dbJob.Status = BuildStatus.Failed;
                    dbJob.CompletedAt = DateTime.UtcNow;
                    db.BuildJobs.Update(dbJob);
                    await db.SaveChangesAsync();
                }

                // Save failed build log to file for diagnostics
                SaveFailedBuildLog(job.Id, job.SpecName, errorLogs);
            }
            catch { /* best effort */ }
        }
        finally
        {
            // Clean up in-memory streaming state (subscribers will get channel completion)
            CleanupLogStreaming(job.Id);
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

    /// <summary>
    /// Save failed build log to a file for diagnostics.
    /// Keeps only the last 5 failed build logs, deleting older ones.
    /// </summary>
    private void SaveFailedBuildLog(Guid jobId, string specName, string logs)
    {
        try
        {
            var failedLogsDir = "/opt/lumina/sources/failed-build-logs";
            Directory.CreateDirectory(failedLogsDir);

            // Create log filename with timestamp and spec name for easy identification
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeSpecName = string.IsNullOrWhiteSpace(specName) ? "unknown" : specName.Replace(".spec", "").Replace("/", "_");
            var logFileName = $"{timestamp}_{safeSpecName}_{jobId:N}.log";
            var logFilePath = Path.Combine(failedLogsDir, logFileName);

            File.WriteAllText(logFilePath, logs);
            _logger.LogInformation("Saved failed build log to {LogFilePath} ({Size} bytes)", logFilePath, logs.Length);

            // Cleanup: keep only last 5 failed build logs
            var existingLogs = new DirectoryInfo(failedLogsDir)
                .GetFiles("*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(5)
                .ToList();

            foreach (var oldLog in existingLogs)
            {
                try
                {
                    oldLog.Delete();
                    _logger.LogDebug("Deleted old failed build log: {FileName}", oldLog.Name);
                }
                catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save failed build log for job {JobId}", jobId);
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