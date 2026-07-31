using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lumina.BuildService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Manages Docker-based builds and publishes their output through the shared
/// backend-neutral log stream hub.
/// </summary>
public class DockerBuildService : IBuildExecutor
{
    public BuildExecutorBackend Backend => BuildExecutorBackend.Docker;

    private readonly DockerClient _docker;
    private readonly BuildDbContext _db;
    private readonly ILogger<DockerBuildService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRpmArtifactValidator _rpmArtifactValidator;
    private readonly BuildExecutionCoordinator _execution;
    private readonly IBuildSlotClaimer _slotClaimer;
    private readonly IBuildLogStreamHub _logStreams;

    /// <summary>
    /// Docker daemon endpoint (unix socket or HTTP proxy URL). When fronted by a
    /// docker-socket-proxy, only the whitelisted endpoints the proxy exposes are
    /// reachable from this service.
    /// </summary>
    private readonly string _dockerUrl;

    /// <summary>Dedicated, isolated bridge network for untrusted build containers. Build containers are attached here instead of the host network so they cannot reach Postgres/RabbitMQ/MinIO/Trivy directly.</summary>
    private readonly string _buildNetwork;

    /// <summary>Host-visible root used for bind mounts passed to the Docker daemon.</summary>
    private readonly string _hostDataRoot;

    /// <summary>Memory cap per build container, in bytes (default 2 GiB).</summary>
    private readonly long _buildMemoryBytes;

    /// <summary>PID limit per build container (default 512) to blunt fork bombs.</summary>
    private readonly long _buildPidsLimit;

    /// <summary>CPU quota per build container in microseconds/period (default 150000 = 1.5 CPUs).</summary>
    private readonly long _buildCpuQuota;

    public DockerBuildService(
        BuildDbContext db,
        ILogger<DockerBuildService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IRpmArtifactValidator rpmArtifactValidator,
        BuildExecutionCoordinator execution,
        IBuildSlotClaimer slotClaimer,
        IBuildLogStreamHub logStreams)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _rpmArtifactValidator = rpmArtifactValidator;
        _execution = execution;
        _slotClaimer = slotClaimer;
        _logStreams = logStreams;

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
        var configuredHostDataRoot = config["Docker:HostDataRoot"] ?? "/opt/lumina";
        if (!Path.IsPathRooted(configuredHostDataRoot))
            throw new InvalidOperationException("Docker:HostDataRoot must be an absolute path.");
        _hostDataRoot = Path.GetFullPath(configuredHostDataRoot);
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

    public virtual async Task<BuildJob> StartBuildAsync(BuildJob job, string? specContent, string? sourceUrl, string? buildImage = null, string? gitUsername = null, string? gitToken = null, string? extraSourcesPipelineDir = null)
    {
        BuildSourceSecurityPolicy.EnsureCredentialFree(sourceUrl, gitUsername, gitToken);

        var imageName = BuildImagePolicy.Resolve(_config, buildImage);
        _logger.LogInformation("Starting Docker build for job {JobId} ({SpecName}) with image {Image}", job.Id, job.SpecName, imageName);
        var ownsExecutionSlot = false;

        try
        {
            await _execution.AcquireAsync(job.Id, recovered: false);
            ownsExecutionSlot = true;

            // Ensure the build image exists locally — try to pull if missing
            var resolvedImage = await EnsureImageExistsAsync(imageName);
            job.RunnerImageReference = imageName;
            job.RunnerImageDigest = resolvedImage.Identity;
            if (!string.Equals(
                    resolvedImage.Architecture,
                    job.TargetArchitecture,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runner '{imageName}' is {resolvedImage.Architecture}, but build {job.Id} targets {job.TargetArchitecture}.");
            }

            await _slotClaimer.ClaimAsync(job);

            // Container-internal path for artifact scanning (matches docker-compose volume mount)
            var artifactDir = $"/app/builds/{job.Id}";
            // Host-side path for RPM container bind mounts (Docker API resolves on host)
            var hostArtifactDir = Path.Combine(_hostDataRoot, "builds", job.Id.ToString());
            Directory.CreateDirectory(artifactDir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    artifactDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.SetGroup);
            }

            var envVars = new List<string>
            {
                $"SPEC_NAME={job.SpecName}",
                $"ARTIFACTS_DIR=/artifacts",
                $"BUILD_JOB_ID={job.Id}",
                $"RUNNER_IMAGE_REFERENCE={imageName}",
                $"RUNNER_IMAGE_IDENTITY={resolvedImage.Identity}",
                $"TARGET_DISTRIBUTION={job.TargetDistribution}",
                $"TARGET_RELEASE={job.TargetRelease}",
                $"TARGET_ARCHITECTURE={job.TargetArchitecture}",
                $"BUILD_PROFILE={job.BuildProfile}",
                "AUTO_DOWNLOAD=true"
            };

            // Write spec content to a file and mount it — avoids "argument list too long" for large specs
            // Uses /opt/lumina/sources which is mounted from the host, so the ephemeral build container can access it
            string? hostSpecDir = null;
            if (!string.IsNullOrEmpty(specContent))
            {
                var specDir = $"/opt/lumina/sources/_specs/{job.Id}";
                hostSpecDir = Path.Combine(_hostDataRoot, "sources", "_specs", job.Id.ToString());
                Directory.CreateDirectory(specDir);
                var specFilePath = Path.Combine(specDir, job.SpecName);
                await File.WriteAllTextAsync(specFilePath, specContent);
                _logger.LogInformation("Spec file written to {SpecFilePath} ({Size} bytes)", specFilePath, specContent.Length);
            }

            if (!string.IsNullOrEmpty(sourceUrl))
                envVars.Add($"SOURCE_URL={sourceUrl}");

            if (!string.IsNullOrEmpty(job.CommitSha))
                envVars.Add($"COMMIT_SHA={job.CommitSha}");

            if (!string.IsNullOrEmpty(job.Branch))
                envVars.Add($"BRANCH={job.Branch}");

            var binds = new List<string>
            {
                $"{hostArtifactDir}:/artifacts:z"
            };

            // Mount spec file into container at /specs/
            if (!string.IsNullOrEmpty(hostSpecDir))
            {
                binds.Add($"{hostSpecDir}:/specs:ro,z");
                _logger.LogInformation("Mounting spec file from {SpecDir} to /specs", hostSpecDir);
            }

            // Mount extra uploaded sources (pipeline-level only — uploaded via the
            // Extra Sources UI into /opt/lumina/extra-sources/pipelines/{pipelineId}/).
            var extraDir = $"/opt/lumina/extra-sources/_builds/{job.Id}";
            var hostExtraDir = Path.Combine(_hostDataRoot, "extra-sources", "_builds", job.Id.ToString());
            if (!string.IsNullOrEmpty(extraSourcesPipelineDir) && Directory.Exists(extraSourcesPipelineDir))
            {
                Directory.CreateDirectory(extraDir);

                // Copy all pipeline extra sources preserving subdirectory structure
                foreach (var dir in Directory.GetDirectories(extraSourcesPipelineDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(extraSourcesPipelineDir, dir);
                    Directory.CreateDirectory(Path.Combine(extraDir, relPath));
                }
                foreach (var file in Directory.GetFiles(extraSourcesPipelineDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(extraSourcesPipelineDir, file);
                    var dest = Path.Combine(extraDir, relPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                }
                _logger.LogInformation("Copied pipeline extra sources from {Dir}", extraSourcesPipelineDir);

                binds.Add($"{hostExtraDir}:/extra-sources:ro,z");
                _logger.LogInformation("Mounted extra sources at /extra-sources for job {JobId}", job.Id);
            }

            // Build-container hardening. These containers run attacker-influenced
            // .spec files (arbitrary macro and shell execution),
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
                // Resolve a configured alias once, then create by immutable image
                // ID. Retagging the alias between inspection and container
                // creation cannot change the bytes used by this job.
                Image = resolvedImage.Identity,
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

            _ = ObserveMonitorAsync(job, container.ID);

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start build for job {JobId}", job.Id);
            job.Status = BuildStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync();
            if (ownsExecutionSlot)
                _execution.Release(job.Id);
            throw;
        }
    }

    private async Task ObserveMonitorAsync(BuildJob job, string containerId)
    {
        try
        {
            await MonitorBuildAsync(job, containerId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Build monitor task escaped for job {JobId}", job.Id);
        }
    }

    public async Task MonitorBuildAsync(
        BuildJob job,
        string containerId,
        CancellationToken stoppingToken = default)
    {
        var logBuilder = new System.Text.StringBuilder();
        var logLock = new object();
        var timedOut = false;
        var leaseLost = 0;

        _logStreams.Start(job.Id, job.Logs ?? string.Empty);

        try
        {
            // Stream logs in parallel with waiting for container completion
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var waitTask = _docker.Containers.WaitContainerAsync(containerId, cts.Token);

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

                        _logStreams.Append(job.Id, logLock, logBuilder, text);
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
            using var dbFlushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var dbFlushTask = Task.Run(async () =>
            {
                try
                {
                    while (!dbFlushCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(_execution.HeartbeatInterval, dbFlushCts.Token);
                        try
                        {
                            string currentLogs;
                            lock (logLock)
                            {
                                currentLogs = logBuilder.ToString();
                            }

                            using var scope = _scopeFactory.CreateScope();
                            var db = scope.ServiceProvider.GetRequiredService<BuildDbContext>();
                            var dbJob = await db.BuildJobs.FindAsync(job.Id);
                            if (dbJob == null || dbJob.LeaseOwner != _execution.WorkerId)
                            {
                                Interlocked.Exchange(ref leaseLost, 1);
                                cts.Cancel();
                                dbFlushCts.Cancel();
                                return;
                            }

                            if (dbJob.Status == BuildStatus.Building)
                            {
                                if (!string.IsNullOrEmpty(currentLogs))
                                    dbJob.Logs = currentLogs;
                                dbJob.LastHeartbeatAt = DateTime.UtcNow;
                                dbJob.LeaseExpiresAt = dbJob.LastHeartbeatAt + _execution.LeaseDuration;
                                await db.SaveChangesAsync();
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
            var deadline = job.DeadlineAt ?? job.StartedAt?.Add(_execution.MaxBuildDuration)
                ?? DateTime.UtcNow.Add(_execution.MaxBuildDuration);
            var remaining = deadline - DateTime.UtcNow;
            ContainerWaitResponse waitResult;
            try
            {
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException();
                waitResult = await waitTask.WaitAsync(remaining, stoppingToken);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                _logger.LogError("Build {JobId} exceeded its wall-clock deadline {Deadline}", job.Id, deadline);
                _logStreams.Publish(job.Id, $"[BUILD TIMED OUT after {_execution.MaxBuildDuration}]");
                await _docker.Containers.StopContainerAsync(
                    containerId,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 10 },
                    stoppingToken);
                waitResult = await waitTask.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }

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
                    // Update with more complete logs from final read. Keep the
                    // in-memory buffer bounded: persist the full finalLogs to the
                    // DB below, but only mirror the tail (up to the cap) into the
                    // shared SSE buffer.
                    lock (logLock)
                    {
                        logBuilder.Clear();
                        logBuilder.Append(finalLogs);
                        if (logBuilder.Length > _logStreams.MaximumBufferCharacters)
                        {
                            logBuilder.Remove(0, logBuilder.Length - _logStreams.MaximumBufferCharacters);
                        }
                        _logStreams.UpdateSnapshot(job.Id, logBuilder.ToString());
                    }
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
            dbJob.LeaseOwner = null;
            dbJob.LeaseExpiresAt = null;
            dbJob.LastHeartbeatAt = DateTime.UtcNow;

            if (dbJob.Status == BuildStatus.Cancelled)
            {
                await db.SaveChangesAsync();
                _logStreams.Publish(job.Id, "[BUILD CANCELLED]");
                return;
            }

            if (timedOut)
            {
                dbJob.Status = BuildStatus.Failed;
                SaveFailedBuildLog(job.Id, job.SpecName, logs);
            }
            else if (waitResult.StatusCode == 0)
            {
                try
                {
                    var artifactCount = await ScanArtifactsAsync(db, dbJob);
                    if (artifactCount == 0)
                    {
                        dbJob.Status = BuildStatus.Failed;
                        _logger.LogWarning(
                            "Build job {JobId} exited successfully but produced no valid RPM artifacts",
                            job.Id);
                        _logStreams.Publish(job.Id, "[BUILD FAILED - no valid RPM artifacts]");
                        SaveFailedBuildLog(job.Id, job.SpecName, logs);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Build step for job {JobId} completed successfully with {ArtifactCount} valid RPM artifact(s)",
                            job.Id, artifactCount);
                    }
                }
                catch (Exception ex)
                {
                    dbJob.Status = BuildStatus.Failed;
                    _logger.LogError(ex, "Artifact validation failed for job {JobId}", job.Id);
                    _logStreams.Publish(job.Id, "[BUILD FAILED - artifact validation error]");
                    SaveFailedBuildLog(job.Id, job.SpecName, logs);
                }
            }
            else
            {
                dbJob.Status = BuildStatus.Failed;
                _logger.LogWarning("Build job {JobId} failed with exit code {ExitCode}", job.Id, waitResult.StatusCode);

                // Save failed build log to file for diagnostics
                SaveFailedBuildLog(job.Id, job.SpecName, logs);
            }

            db.BuildJobs.Update(dbJob);
            await db.SaveChangesAsync();

            // Notify SSE subscribers that build is complete
            var buildResult = dbJob.Status == BuildStatus.Building ? "SUCCESS" : "FAILED";
            _logStreams.Publish(job.Id, $"[BUILD {buildResult}]");

            var coordinator = scope.ServiceProvider.GetRequiredService<PipelineRunCoordinator>();
            if (dbJob.Status == BuildStatus.Building)
            {
                await coordinator.CompleteBuildStepAsync(dbJob.Id);
            }
            else
            {
                await coordinator.FailStepAsync(
                    dbJob.Id,
                    StepType.Build,
                    timedOut
                        ? $"The build exceeded its {_execution.MaxBuildDuration} wall-clock limit."
                        : waitResult.StatusCode == 0
                        ? "The build produced no valid RPM artifacts."
                        : $"The build container exited with code {waitResult.StatusCode}.");
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
        catch (OperationCanceledException) when (Volatile.Read(ref leaseLost) == 1)
        {
            _logger.LogWarning(
                "Build monitor for job {JobId} lost its lease and relinquished the container",
                job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Build monitor for job {JobId} is stopping; its lease will be reclaimed after restart",
                job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring build job {JobId}", job.Id);

            // Notify subscribers about the error
            _logStreams.Publish(job.Id, "[BUILD ERROR - monitoring failed]");
            try
            {
                lock (logLock)
                {
                    if (logBuilder.Length > _logStreams.MaximumBufferCharacters)
                        logBuilder.Remove(0, logBuilder.Length - _logStreams.MaximumBufferCharacters);
                    _logStreams.UpdateSnapshot(job.Id, logBuilder.ToString());
                }
            }
            catch { }

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
                    db.BuildJobs.Update(dbJob);
                    await db.SaveChangesAsync();

                    var coordinator = scope.ServiceProvider.GetRequiredService<PipelineRunCoordinator>();
                    await coordinator.FailStepAsync(
                        dbJob.Id,
                        StepType.Build,
                        "Container monitoring failed.");
                }

                // Save failed build log to file for diagnostics
                SaveFailedBuildLog(job.Id, job.SpecName, errorLogs);
            }
            catch { /* best effort */ }
        }
        finally
        {
            _execution.Release(job.Id);
            // Clean up in-memory streaming state (subscribers will get channel completion)
            _logStreams.Complete(job.Id);
        }
    }

    Task IBuildExecutor.MonitorBuildAsync(
        BuildJob job,
        CancellationToken cancellationToken)
    {
        if (job.ExecutionBackend != BuildExecutorBackend.Docker)
            throw new BuildExecutorIdentityException("Docker executor cannot monitor a non-Docker build.");
        if (string.IsNullOrWhiteSpace(job.ContainerId))
            throw new BuildExecutorIdentityException("Docker build has no recorded container identity.");
        return MonitorBuildAsync(job, job.ContainerId, cancellationToken);
    }

    /// <summary>
    /// Scan the artifacts directory for built .rpm files and create BuildArtifact records.
    /// </summary>
    private async Task<int> ScanArtifactsAsync(BuildDbContext db, BuildJob job)
    {
        var artifactDir = $"/app/builds/{job.Id}";
        if (!Directory.Exists(artifactDir))
        {
            _logger.LogWarning("Artifact directory {Dir} does not exist for job {JobId}", artifactDir, job.Id);
            return 0;
        }

        var rpmFiles = Directory
            .GetFiles(artifactDir, "*.rpm", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".src.rpm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _logger.LogInformation("Found {Count} RPM artifacts for job {JobId}", rpmFiles.Length, job.Id);

        var registeredCount = 0;
        foreach (var rpmPath in rpmFiles)
        {
            var fileName = Path.GetFileName(rpmPath);
            var fileInfo = new FileInfo(rpmPath);
            var validation = await _rpmArtifactValidator.ValidateAsync(rpmPath);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "Ignoring invalid RPM artifact {FileName} for job {JobId}: {ValidationError}",
                    fileName, job.Id, validation.Error);
                continue;
            }

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
                // The immutable object key is populated after SecurityService
                // embeds and verifies the RPM signature.
                StoragePath = string.Empty,
                CveScanStatus = ScanStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            db.BuildArtifacts.Add(artifact);
            registeredCount++;
            _logger.LogInformation(
                "Registered RPM artifact {FileName} ({Nevra}, {Size} bytes, SHA256: {Hash}) for job {JobId}",
                fileName, validation.Nevra, fileInfo.Length, hashSha256[..16] + "...", job.Id);
        }

        await db.SaveChangesAsync();
        return registeredCount;
    }

    /// <summary>
    /// Ensure the specified Docker image exists locally. If not found, attempt to pull it.
    /// This prevents build failures when images haven't been pre-built on a new device.
    /// </summary>
    private async Task<ResolvedBuildImage> EnsureImageExistsAsync(string imageName)
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
                return await ResolveImageAsync(images[0], imageName);
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
                var pulledImages = await _docker.Images.ListImagesAsync(new ImagesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                    }
                });
                return pulledImages.Count > 0
                    ? await ResolveImageAsync(pulledImages[0], imageName)
                    : throw new InvalidOperationException(
                        $"Pulled build image '{imageName}' could not be resolved to an immutable image ID.");
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve build image {Image} to an immutable identity", imageName);
            throw new InvalidOperationException(
                $"Build image '{imageName}' could not be verified.", ex);
        }
    }

    private async Task<ResolvedBuildImage> ResolveImageAsync(
        ImagesListResponse image,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(image.ID) ||
            !image.ID.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Build image '{reference}' has no immutable Docker image ID.");
        }

        var inspected = await _docker.Images.InspectImageAsync(image.ID);
        var architecture = inspected.Architecture switch
        {
            "amd64" => "x86_64",
            "arm64" => "aarch64",
            _ => inspected.Architecture
        };
        if (string.IsNullOrWhiteSpace(architecture))
        {
            throw new InvalidOperationException(
                $"Build image '{reference}' has no architecture metadata.");
        }

        return new ResolvedBuildImage(image.ID, architecture);
    }

    private sealed record ResolvedBuildImage(string Identity, string Architecture);

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

    public async Task<bool> CancelBuildAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await _db.BuildJobs
            .Include(item => item.StepRuns)
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job == null) return false;
        if (job.ExecutionBackend != BuildExecutorBackend.Docker) return false;
        if (job.Status != BuildStatus.Queued && job.Status != BuildStatus.Building) return false;

        try
        {
            // Stop Docker container if one exists
            if (!string.IsNullOrEmpty(job.ContainerId))
            {
                try
                {
                    await _docker.Containers.StopContainerAsync(
                        job.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                        cancellationToken);
                    _logger.LogInformation("Stopped container {ContainerId} for cancelled build {BuildId}", job.ContainerId, jobId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop container {ContainerId} for build {BuildId}", job.ContainerId, jobId);
                }

                // Remove the container
                try
                {
                    await _docker.Containers.RemoveContainerAsync(
                        job.ContainerId,
                        new ContainerRemoveParameters { Force = true },
                        cancellationToken);
                }
                catch { /* best effort */ }
            }

            var previousStatus = job.Status.ToString();
            job.Status = BuildStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            foreach (var step in job.StepRuns.Where(step =>
                         step.Status is StepStatus.Pending or StepStatus.Running))
            {
                step.Status = StepStatus.Skipped;
                step.CompletedAt = DateTime.UtcNow;
                step.Error = "Build cancelled.";
            }
            _db.BuildJobs.Update(job);
            await _db.SaveChangesAsync(cancellationToken);
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
