using System.Security.Cryptography;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.BuildService.Services.PackageGraph;

public interface IProjectLookasideSourceSealer
{
    Task SealAsync(ProjectDispatchPlan plan, CancellationToken cancellationToken);
}

internal interface IProjectLookasideObjectStore
{
    Task UploadAsync(
        string objectName,
        string sourcePath,
        long expectedSize,
        CancellationToken cancellationToken);
}

internal sealed class ProjectLookasideObjectStore(
    IMinioClient minio,
    ArtifactStorageService artifactStorage) : IProjectLookasideObjectStore
{
    public async Task UploadAsync(
        string objectName,
        string sourcePath,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await artifactStorage.EnsureBucketAsync(cancellationToken);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedSize)
            throw new ValidationException("Verified lookaside source size changed before upload.");
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(ArtifactStorageService.BucketName)
            .WithObject(objectName)
            .WithStreamData(source)
            .WithObjectSize(source.Length)
            .WithContentType("application/octet-stream"), cancellationToken);
    }
}

/// <summary>
/// Copies administrator-uploaded pipeline sources into verified,
/// content-addressed objects before a dispatch plan can become runnable.
/// </summary>
internal sealed class ProjectLookasideSourceSealer : IProjectLookasideSourceSealer
{
    private const string DefaultPipelinesRoot = "/opt/lumina/extra-sources/pipelines";
    private readonly string _pipelinesRoot;
    private readonly IProjectLookasideObjectStore _objects;
    private readonly ILogger<ProjectLookasideSourceSealer> _logger;

    public ProjectLookasideSourceSealer(
        IConfiguration configuration,
        IProjectLookasideObjectStore objects,
        ILogger<ProjectLookasideSourceSealer> logger)
    {
        _pipelinesRoot = Path.GetFullPath(
            configuration["ExtraSources:PipelinesRoot"]?.Trim() ?? DefaultPipelinesRoot);
        _objects = objects;
        _logger = logger;
    }

    public async Task SealAsync(ProjectDispatchPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (var target in plan.Stages.SelectMany(stage => stage.Targets))
        {
            ProjectLookasideSourcePolicy.Validate(target.LookasideSources);
            foreach (var source in target.LookasideSources ?? [])
                await SealAsync(target, source, cancellationToken);
        }
    }

    private async Task SealAsync(
        ProjectDispatchTarget target,
        ProjectLookasideSource source,
        CancellationToken cancellationToken)
    {
        var localPath = ProcessArgumentSanitizer.ResolveConfinedPath(
            Path.Combine(target.PipelineId.ToString(), "pipeline", source.FileName),
            _pipelinesRoot);
        RejectSymbolicLinks(localPath);

        var snapshotDirectory = Path.Combine(
            Path.GetTempPath(), "lumina-lookaside-snapshots", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshotPath = Path.Combine(snapshotDirectory, source.FileName);
        try
        {
            try
            {
                await using var input = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != source.Size)
                    throw new ValidationException(
                        $"Lookaside source '{source.FileName}' does not match its declared size.");
                await using var snapshot = new FileStream(
                    snapshotPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(snapshot, cancellationToken);
            }
            catch (ValidationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ValidationException(
                    $"Lookaside source '{source.FileName}' is unavailable for pipeline '{target.PipelineId}'.",
                    exception);
            }

            await using (var snapshot = new FileStream(
                             snapshotPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (snapshot.Length != source.Size)
                    throw new ValidationException(
                        $"Lookaside source '{source.FileName}' changed while it was being sealed.");
                var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(snapshot, cancellationToken))
                    .ToLowerInvariant();
                if (!string.Equals(hash, source.Sha256, StringComparison.Ordinal))
                    throw new ValidationException(
                        $"Lookaside source '{source.FileName}' does not match its declared SHA-256.");
            }

            await _objects.UploadAsync(
                source.ObjectName,
                snapshotPath,
                source.Size,
                cancellationToken);
            _logger.LogInformation(
                "Sealed lookaside source {FileName} for package {PackageId} as {ObjectName}",
                source.FileName,
                target.PackageId,
                source.ObjectName);
        }
        finally
        {
            if (Directory.Exists(snapshotDirectory))
                Directory.Delete(snapshotDirectory, recursive: true);
        }
    }

    private void RejectSymbolicLinks(string path)
    {
        var relative = Path.GetRelativePath(_pipelinesRoot, path);
        var current = _pipelinesRoot;
        try
        {
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ValidationException("Lookaside source paths may not contain symbolic links.");
            }
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException("Lookaside source path is unavailable.", exception);
        }
    }
}
