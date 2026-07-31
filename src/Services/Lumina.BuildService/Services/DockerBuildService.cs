using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Lumina.BuildService.Data;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Services;

/// <summary>
/// Manages Docker-based builds with real-time log streaming.
/// Logs are streamed from Docker containers as they arrive and made available
/// to SSE subscribers via Channel-based pub/sub.
/// </summary>
public class DockerBuildService : IBuildLauncher
{
    public BuildExecutorBackend Backend => BuildExecutorBackend.Docker;

    private readonly DockerClient _docker;
    private readonly BuildDbContext _db;
    private readonly ILogger<DockerBuildService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRpmArtifactValidator _rpmArtifactValidator;
    private readonly BuildExecutionCoordinator _execution;

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

    /// <summary>
    /// Hard cap on the in-memory log buffer kept per build job, in characters. Once a
    /// build's accumulated logs exceed this, the buffer is truncated from the head
    /// (newest tail retained) so memory stays bounded regardless of how verbose
    /// <c>dnf builddep</c> gets. The full log is still persisted to the database and
    /// to the failed-build-log file on disk; only the in-memory SSE buffer is bounded.
    /// </summary>
    private readonly long _maxLogBufferChars;

    /// <summary>
    /// Maximum number of concurrent live SSE subscribers across the whole service.
    /// Each subscriber holds a channel and an open response stream; an unbounded
    /// count is an easy DoS amplification point (a few clients opening many streams).
    /// Once the limit is reached, new live-stream requests fall back to replay mode.
    /// </summary>
    private readonly int _maxConcurrentSseSubscribers;

    /// <summary>
    /// Capacity of each per-subscriber SSE channel. Bounded so a slow client cannot
    /// force the service to buffer unbounded log lines in its channel; when full,
    /// new lines are dropped (the slow client lags but the build keeps streaming).
    /// </summary>
    private readonly int _sseChannelCapacity;

    /// <summary>
    /// Global semaphore gating live SSE subscribers across all builds. Counts
    /// subscribers process-wide (the field is static because the dictionaries below
    /// are static and shared across all DI scopes of this service).
    /// </summary>
    private static SemaphoreSlim? _sseSubscriberGate;

    /// <summary>Full accumulated logs per build job (in-memory buffer, head-truncated at <see cref="_maxLogBufferChars"/>).</summary>
    private static readonly ConcurrentDictionary<Guid, string> _logBuffers = new();

    /// <summary>Subscribers per build job that receive new log lines in real-time.</summary>
    private static readonly ConcurrentDictionary<Guid, List<Channel<string>>> _subscribers = new();

    public DockerBuildService(
        BuildDbContext db,
        ILogger<DockerBuildService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        IRpmArtifactValidator rpmArtifactValidator,
        BuildExecutionCoordinator execution)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
        _rpmArtifactValidator = rpmArtifactValidator;
        _execution = execution;

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

        // Log-streaming resource bounds. The static dictionaries and the subscriber
        // gate outlive any single DI scope (the service is scoped but the streaming
        // state is deliberately process-global so MonitorBuildAsync can keep
        // publishing after the triggering request ends), so their limits must be
        // process-global too. Initialize the gate once from the first constructor
        // call; a consistent config is assumed across scopes.
        _maxLogBufferChars = ParseLongConfig(config, "BuildStreaming:MaxLogBufferChars", 2L * 1024 * 1024); // 2 MiB chars
        _maxConcurrentSseSubscribers = (int)ParseLongConfig(config, "BuildStreaming:MaxConcurrentSseSubscribers", 64);
        _sseChannelCapacity = (int)ParseLongConfig(config, "BuildStreaming:SseChannelCapacity", 1024);

        if (_maxConcurrentSseSubscribers <= 0) _maxConcurrentSseSubscribers = 64;
        if (_sseChannelCapacity <= 0) _sseChannelCapacity = 1024;
        if (_maxLogBufferChars <= 0) _maxLogBufferChars = 2L * 1024 * 1024;

        // Lazily create the global subscriber gate. The dictionaries above are
        // initialized once per process; do the same for the semaphore so the limit
        // reflects the configured value rather than being reset per scope.
        if (_sseSubscriberGate is null)
        {
            Interlocked.CompareExchange(
                ref _sseSubscriberGate,
                new SemaphoreSlim(_maxConcurrentSseSubscribers, _maxConcurrentSseSubscribers),
                null);
        }
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
        var job = await _db.BuildJobs
            .Include(item => item.StepRuns)
            .SingleOrDefaultAsync(item => item.Id == jobId);
        return job?.Logs ?? "";
    }

    /// <summary>
    /// Result of an attempt to subscribe to a build's live log stream.
    /// </summary>
    public sealed record LogSubscription(
        bool IsLive,            // true only if a live channel was actually attached
        string ExistingLogs,    // accumulated logs captured atomically with the (attempted) attach
        ChannelReader<string>? Reader,
        bool SubscriberLimitReached); // true when the global SSE gate was full and live attach was refused

    /// <summary>
    /// Attempt to subscribe to real-time log updates for a build job.
    ///
    /// The "is this build currently streaming?" decision and the actual channel
    /// registration are performed atomically (under the per-build subscriber lock
    /// while re-checking the live buffer), eliminating the TOCTOU window where a
    /// build could finish between <see cref="IsStreaming"/> and the old
    /// <c>SubscribeToLogs</c>. Callers that get <see cref="LogSubscription.IsLive"/>
    /// == false must fall back to replay.
    ///
    /// A global semaphore bounds the number of concurrent live subscribers; once it
    /// is exhausted, requests are downgraded to replay rather than rejected, so a
    /// flood of clients cannot exhaust memory.
    /// </summary>
    public async Task<LogSubscription> SubscribeToLogsAsync(Guid jobId)
    {
        var gate = _sseSubscriberGate!;
        var entered = await gate.WaitAsync(TimeSpan.Zero);

        if (!entered)
        {
            // Global SSE subscriber limit reached: do not hold a live slot. Return
            // whatever buffer we currently have so the caller can replay it.
            var snapshot = _logBuffers.TryGetValue(jobId, out var buf) ? buf : "";
            return new LogSubscription(IsLive: false, ExistingLogs: snapshot, Reader: null, SubscriberLimitReached: true);
        }

        // We hold a slot in the global gate from here until UnsubscribeFromLogs.
        // Register the channel atomically with re-checking the live buffer so a
        // build cannot complete (and drop its buffer) between the check and the
        // subscription.
        Channel<string>? channel = null;
        string existingLogs;
        bool isLive;

        var list = _subscribers.GetOrAdd(jobId, _ => new List<Channel<string>>());
        lock (list)
        {
            existingLogs = _logBuffers.TryGetValue(jobId, out var buf) ? buf : "";
            isLive = existingLogs.Length > 0 || _logBuffers.ContainsKey(jobId);

            if (!isLive)
            {
                // The build is not (or no longer) streaming live. Don't attach a
                // channel that would never receive [BUILD ...]; let the caller
                // replay the existing logs instead.
                if (list.Count == 0)
                    _subscribers.TryRemove(jobId, out _);
            }
            else
            {
                channel = Channel.CreateBounded<string>(new BoundedChannelOptions(_sseChannelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    // Drop oldest lines for a slow client rather than blocking the
                    // writer (the streaming task) or growing without bound.
                    FullMode = BoundedChannelFullMode.DropOldest
                });
                list.Add(channel);
            }
        }

        if (channel is null)
        {
            // Not live — release the gate slot we briefly held.
            gate.Release();
        }

        return new LogSubscription(
            IsLive: channel is not null,
            ExistingLogs: existingLogs,
            Reader: channel?.Reader,
            SubscriberLimitReached: false);
    }

    /// <summary>
    /// Unsubscribe from log updates. Must be called exactly once for each
    /// successful live subscription (i.e. when <see cref="LogSubscription.IsLive"/>
    /// is true) when the SSE connection closes, to release the global subscriber
    /// slot. Safe to call for non-live subscriptions (no-op).
    /// </summary>
    public void UnsubscribeFromLogs(Guid jobId, ChannelReader<string>? reader)
    {
        if (reader is null) return; // non-live subscription — nothing to release

        if (_subscribers.TryGetValue(jobId, out var list))
        {
            lock (list)
            {
                list.RemoveAll(ch => ReferenceEquals(ch.Reader, reader));
                if (list.Count == 0)
                    _subscribers.TryRemove(jobId, out _);
            }
        }

        // Release the global SSE slot acquired in SubscribeToLogsAsync. Wrap in
        // try/catch: a duplicate or rogue release throws SemaphoreFullException,
        // which must not crash the streaming pipeline.
        try { _sseSubscriberGate!.Release(); }
        catch (SemaphoreFullException) { /* over-release guard */ }
    }

    /// <summary>
    /// Check if a build is currently being monitored (streaming logs).
    ///
    /// NOTE: this is only suitable for diagnostics/monitoring. SSE endpoints must
    /// NOT use this to decide between live and replay — that decision must be made
    /// atomically with the subscription via <see cref="SubscribeToLogsAsync"/> to
    /// avoid a TOCTOU race.
    /// </summary>
    public static bool IsStreaming(Guid jobId) => _logBuffers.ContainsKey(jobId);

    private void PublishLogLine(Guid jobId, string line)
    {
        if (!_subscribers.TryGetValue(jobId, out var list)) return;
        lock (list)
        {
            foreach (var channel in list)
            {
                // Bounded channel: TryWrite cannot block; with DropOldest it
                // returns true and discards the oldest pending item when full.
                channel.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>
    /// Append <paramref name="text"/> to the per-build log builder and mirror the
    /// truncated result into the shared in-memory buffer, then return the list of
    /// newly completed lines to publish to subscribers. Caller must hold the
    /// per-build <paramref name="logLock"/>; the only work done under the lock is
    /// the append, the head-truncation, and a single substring over the appended
    /// chunk (no full ToString() of the entire buffer).
    /// </summary>
    private List<string> AppendLogChunk(Guid jobId, object logLock, System.Text.StringBuilder logBuilder, string text)
    {
        lock (logLock)
        {
            logBuilder.Append(text);

            // Enforce the per-build buffer cap by truncating the head. Done in the
            // same lock that owns logBuilder so the buffer and the StringBuilder
            // never diverge. The cap is in characters, not bytes; this keeps the
            // in-memory SSE buffer bounded. The DB persists the full (untruncated)
            // log from a separate flush of logBuilder, which is also bounded by the
            // same Remove below, so DB and memory stay consistent.
            if (logBuilder.Length > _maxLogBufferChars)
            {
                var overflow = logBuilder.Length - (int)_maxLogBufferChars;
                logBuilder.Remove(0, overflow);
            }

            // Snapshot once for the shared buffer (subscribers read this on attach).
            _logBuffers[jobId] = logBuilder.ToString();

            // Only the just-appended chunk is split into lines; we never resplit
            // the whole buffer. This is O(chunk size), independent of total log
            // length, fixing the previous O(n^2) behavior.
            return text.Split('\n').ToList();
        }
    }

    private void CleanupLogStreaming(Guid jobId)
    {
        // Atomically tear down streaming state for this job. Taking the per-build
        // subscriber lock here is what closes the TOCTOU window: SubscribeToLogsAsync
        // reads _logBuffers under this same lock, so any subscriber that observes
        // isLive=true is guaranteed to already be in `snapshot` when we complete the
        // channels below — it can never end up on a channel that misses both the
        // [BUILD ...] marker and channel completion.
        var list = _subscribers.GetOrAdd(jobId, _ => new List<Channel<string>>());
        List<Channel<string>> snapshot;
        lock (list)
        {
            _logBuffers.TryRemove(jobId, out _);
            snapshot = list.ToList();
            list.Clear();
        }
        _subscribers.TryRemove(jobId, out _);

        // Completing the channel unblocks any subscriber currently awaiting
        // ReadAllAsync, which then runs its own finally -> UnsubscribeFromLogs,
        // where the global SSE slot is released exactly once. We must NOT release
        // the gate here, or that single UnsubscribeFromLogs release would be a
        // double release (SemaphoreFullException).
        foreach (var channel in snapshot)
        {
            channel.Writer.TryComplete();
        }
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

            await AcquireDistributedBuildSlotAsync(job);

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

    private async Task AcquireDistributedBuildSlotAsync(BuildJob job)
    {
        while (true)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            // Serialize the count-and-claim across every BuildService replica.
            // The fixed key is private to Lumina's build-slot allocation.
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4867564)");

            var now = DateTime.UtcNow;
            var active = await _db.BuildJobs.CountAsync(item =>
                item.Status == BuildStatus.Building
                && item.LeaseExpiresAt != null
                && item.LeaseExpiresAt > now);
            if (active < _execution.MaxConcurrentBuilds)
            {
                job.Status = BuildStatus.Building;
                job.StartedAt = now;
                job.LeaseOwner = _execution.WorkerId;
                job.LastHeartbeatAt = now;
                job.LeaseExpiresAt = now + _execution.LeaseDuration;
                job.DeadlineAt = now + _execution.MaxBuildDuration;
                _db.BuildJobs.Update(job);
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
                return;
            }

            await transaction.RollbackAsync();
            await Task.Delay(_execution.HeartbeatInterval);
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

        // Initialize the in-memory log buffer for SSE subscribers
        _logBuffers[job.Id] = "";

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

                        // Append to log builder and update buffer. AppendLogChunk
                        // enforces the buffer cap and returns just the new lines —
                        // no full-buffer rebuild or resplit, so cost is O(chunk).
                        List<string> newLines = AppendLogChunk(job.Id, logLock, logBuilder, text);

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
                PublishLogLine(job.Id, $"[BUILD TIMED OUT after {_execution.MaxBuildDuration}]");
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
                        if (logBuilder.Length > _maxLogBufferChars)
                        {
                            logBuilder.Remove(0, logBuilder.Length - (int)_maxLogBufferChars);
                        }
                        _logBuffers[job.Id] = logBuilder.ToString();
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
                PublishLogLine(job.Id, "[BUILD CANCELLED]");
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
                        PublishLogLine(job.Id, "[BUILD FAILED - no valid RPM artifacts]");
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
                    PublishLogLine(job.Id, "[BUILD FAILED - artifact validation error]");
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
            PublishLogLine(job.Id, $"[BUILD {buildResult}]");

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
            PublishLogLine(job.Id, "[BUILD ERROR - monitoring failed]");
            try
            {
                lock (logLock)
                {
                    if (logBuilder.Length > _maxLogBufferChars)
                        logBuilder.Remove(0, logBuilder.Length - (int)_maxLogBufferChars);
                    _logBuffers[job.Id] = logBuilder.ToString();
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
            CleanupLogStreaming(job.Id);
        }
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
            foreach (var step in job.StepRuns.Where(step =>
                         step.Status is StepStatus.Pending or StepStatus.Running))
            {
                step.Status = StepStatus.Skipped;
                step.CompletedAt = DateTime.UtcNow;
                step.Error = "Build cancelled.";
            }
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
