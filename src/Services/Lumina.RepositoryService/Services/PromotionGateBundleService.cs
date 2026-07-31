using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.RepositoryService.Data;
using Lumina.Shared.Errors;
using Lumina.Shared.Events;
using Lumina.Shared.Models;
using Minio;
using Minio.DataModel.Args;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Services;

internal sealed record PromotionGateBundleManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("promotionSetId")] Guid PromotionSetId,
    [property: JsonPropertyName("repositoryId")] Guid RepositoryId,
    [property: JsonPropertyName("candidateManifestSha256")] string CandidateManifestSha256,
    [property: JsonPropertyName("baselineManifestSha256")] string BaselineManifestSha256,
    [property: JsonPropertyName("targetArchitecture")] string TargetArchitecture,
    [property: JsonPropertyName("baselinePackageNames")] IReadOnlyList<string> BaselinePackageNames,
    [property: JsonPropertyName("candidates")] IReadOnlyList<PromotionGateCandidateInput> Candidates);

public sealed class PromotionGateBundleService(
    RepositoryDbContext db,
    RepositoryManagerService repositories,
    IMinioClient minio,
    ILogger<PromotionGateBundleService> logger)
{
    private const string ArtifactBucket = "lumina-artifacts";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PromotionGatePrepared> PrepareAsync(
        PromotionGatePreparationRequested request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var repository = await db.Repositories
            .FromSqlInterpolated(
                $"""SELECT * FROM "Repositories" WHERE "Id" = {request.RepositoryId} FOR UPDATE""")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ValidationException("Promotion gate repository was not found.");
        var set = await db.PromotionSets.Include(item => item.Packages)
            .SingleOrDefaultAsync(item => item.Id == request.PromotionSetId &&
                                          item.RepositoryId == request.RepositoryId,
                cancellationToken)
            ?? throw new ValidationException("Promotion gate set was not found.");
        ValidateCandidates(set, request.Candidates);
        if (set.GateBundleObjectName is not null)
        {
            if (set.GateCandidateManifestSha256 != request.CandidateManifestSha256)
                throw new ConflictException("Promotion gate candidate manifest changed after preparation.");
            await transaction.CommitAsync(cancellationToken);
            return Prepared(set, request.CandidateManifestSha256);
        }
        if (set.Status != Lumina.Shared.Models.Enums.PromotionSetStatus.Candidate)
            throw new ConflictException("Promotion gate set no longer accepts bundle preparation.");

        var staging = repositories.CreatePublicationStagingDirectory(set.RepositoryId);
        try
        {
            var root = Path.Combine(staging, "gate-bundle");
            var baselineDirectory = Path.Combine(root, "baseline");
            var candidateDirectory = Path.Combine(root, "candidates");
            Directory.CreateDirectory(baselineDirectory);
            Directory.CreateDirectory(candidateDirectory);

            var livePackages = await db.Packages.AsNoTracking()
                .Where(item => item.RepositoryId == set.RepositoryId && item.Status == "Ready" &&
                               (item.Arch == set.TargetArchitecture || item.Arch == "noarch"))
                .OrderBy(item => item.FileName)
                .ToListAsync(cancellationToken);
            foreach (var package in livePackages)
            {
                var source = repositories.GetLiveRpmPath(
                    repository.BasePath, package.Arch, package.FileName);
                ValidateFile(source, package.FileSize, package.HashSha256, package.FileName);
                var destination = Path.Combine(baselineDirectory, package.FileName);
                File.Copy(source, destination);
                File.SetLastWriteTimeUtc(destination, DateTime.UnixEpoch);
            }

            foreach (var input in request.Candidates.OrderBy(item => item.FileName, StringComparer.Ordinal))
            {
                var destination = Path.Combine(candidateDirectory, input.FileName);
                await minio.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(ArtifactBucket)
                    .WithObject(input.ObjectName)
                    .WithFile(destination), cancellationToken);
                ValidateFile(destination, input.Size, input.Sha256, input.FileName);
                File.SetLastWriteTimeUtc(destination, DateTime.UnixEpoch);
            }

            var revision = StableRevision(set.Id);
            await repositories.GenerateGateMetadataAsync(baselineDirectory, revision);
            await repositories.GenerateGateMetadataAsync(candidateDirectory, revision);
            var candidateNames = set.Packages.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            var baselineNames = livePackages.Where(item => candidateNames.Contains(item.Name))
                .Select(item => item.Name).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            var baselineManifestSha256 = RepositoryManifestPolicy.Compute(livePackages);
            var manifest = new PromotionGateBundleManifest(
                1, set.Id, set.RepositoryId, request.CandidateManifestSha256,
                baselineManifestSha256,
                set.TargetArchitecture, baselineNames,
                request.Candidates.OrderBy(item => item.ProjectPackageId, StringComparer.Ordinal)
                    .ThenBy(item => item.FileName, StringComparer.Ordinal).ToList());
            await File.WriteAllBytesAsync(
                Path.Combine(root, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Json),
                cancellationToken);

            var bundlePath = Path.Combine(staging, "input.tar");
            await CreateDeterministicTarAsync(root, bundlePath, cancellationToken);
            var info = new FileInfo(bundlePath);
            if (info.Length is <= 0 or > 8L * 1024 * 1024 * 1024)
                throw new ValidationException("Promotion gate bundle exceeds the size policy.");
            await using var bundle = new FileStream(
                bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(bundle, cancellationToken))
                .ToLowerInvariant();
            var objectName = $"promotion-gates/{set.Id:N}/sha256/{hash}/input.tar";
            bundle.Position = 0;
            await EnsureArtifactBucketAsync(cancellationToken);
            await minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(objectName)
                .WithStreamData(bundle)
                .WithObjectSize(bundle.Length)
                .WithContentType("application/x-tar"), cancellationToken);

            var now = DateTime.UtcNow;
            RepositoryPromotionPolicy.RecordGateBundle(
                set, request.CandidateManifestSha256, baselineManifestSha256,
                objectName, hash, bundle.Length, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Prepared immutable promotion gate bundle {ObjectName} for set {PromotionSetId}",
                objectName, set.Id);
            return Prepared(set, request.CandidateManifestSha256);
        }
        finally
        {
            repositories.CleanupPublicationStaging(staging);
        }
    }

    private static void ValidateRequest(PromotionGatePreparationRequested request)
    {
        if (request.PromotionSetId == Guid.Empty || request.RepositoryId == Guid.Empty ||
            request.RequestedAt.Kind != DateTimeKind.Utc ||
            request.CandidateManifestSha256.Length != 64 ||
            request.CandidateManifestSha256.Any(character => !Uri.IsHexDigit(character)) ||
            request.Candidates is not { Count: > 0 and <= 4096 })
            throw new ValidationException("Promotion gate preparation request is invalid.");
        if (request.Candidates.Select(item => item.ArtifactId).Distinct().Count() != request.Candidates.Count ||
            request.Candidates.Select(item => item.CandidatePackageId).Distinct().Count() != request.Candidates.Count ||
            request.Candidates.Sum(item => item.Size) > 8L * 1024 * 1024 * 1024)
            throw new ValidationException("Promotion gate candidate set is ambiguous or oversized.");
        foreach (var candidate in request.Candidates)
        {
            var fileName = Path.GetFileName(candidate.FileName);
            var hash = (candidate.Sha256 ?? string.Empty).ToLowerInvariant();
            _ = RepositoryPromotionPolicy.NormalizePackageId(candidate.ProjectPackageId);
            if (candidate.ArtifactId == Guid.Empty || candidate.CandidatePackageId == Guid.Empty ||
                string.IsNullOrWhiteSpace(fileName) || fileName != candidate.FileName ||
                candidate.Size is <= 0 or > 8L * 1024 * 1024 * 1024 ||
                hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)) ||
                candidate.ObjectName != $"sha256/{hash}/{fileName}")
                throw new ValidationException("Promotion gate candidate input is invalid.");
        }
    }

    private static void ValidateCandidates(
        RepositoryPromotionSet set,
        IReadOnlyList<PromotionGateCandidateInput> requested)
    {
        if (set.Packages.Count != requested.Count)
            throw new ConflictException("Promotion gate candidate membership is incomplete.");
        var packages = set.Packages.ToDictionary(item => item.Id);
        foreach (var input in requested)
        {
            if (!packages.TryGetValue(input.CandidatePackageId, out var package) ||
                package.ArtifactId != input.ArtifactId || package.PromotionPackageId != input.ProjectPackageId ||
                package.FileName != input.FileName || package.CandidateObjectName != input.ObjectName ||
                package.FileSize != input.Size ||
                !string.Equals(package.HashSha256, input.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException("Promotion gate candidate provenance changed.");
        }
    }

    private static void ValidateFile(string path, long expectedSize, string? expectedHash, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || expectedSize <= 0 ||
            expectedHash is not { Length: 64 })
            throw new ValidationException("Promotion gate package metadata is invalid.");
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (info.Length != expectedSize || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException($"Promotion gate package '{fileName}' failed integrity validation.");
    }

    private static long StableRevision(Guid id) =>
        Math.Abs(BitConverter.ToInt64(id.ToByteArray(), 0) % 2_000_000_000L) + 1;

    private async Task EnsureArtifactBucketAsync(CancellationToken cancellationToken)
    {
        if (await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(ArtifactBucket), cancellationToken))
            return;
        await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(ArtifactBucket), cancellationToken);
    }

    private static PromotionGatePrepared Prepared(RepositoryPromotionSet set, string manifestHash) => new(
        set.Id, set.RepositoryId, manifestHash, set.GateBundleObjectName!,
        set.GateBundleSha256!, set.GateBundleSize!.Value, set.GateBundlePreparedAt!.Value);

    private static async Task CreateDeterministicTarAsync(
        string root,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var entry = new PaxTarEntry(
                Directory.Exists(path) ? TarEntryType.Directory : TarEntryType.RegularFile,
                relative)
            {
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                Gid = 0,
                Mode = (UnixFileMode)(Directory.Exists(path)
                    ? Convert.ToInt32("755", 8)
                    : Convert.ToInt32("644", 8))
            };
            if (!Directory.Exists(path))
                entry.DataStream = File.OpenRead(path);
            writer.WriteEntry(entry);
            entry.DataStream?.Dispose();
        }
        await output.FlushAsync(cancellationToken);
    }
}
