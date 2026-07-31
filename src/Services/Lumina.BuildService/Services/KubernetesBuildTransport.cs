using System.Text.Json;
using System.Text.Json.Serialization;
using k8s.Models;
using Lumina.BuildService.Services.PackageGraph;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Minio;
using Minio.DataModel.Args;

namespace Lumina.BuildService.Services;

public sealed record KubernetesBuildTransport(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("buildJobId")] Guid BuildJobId,
    [property: JsonPropertyName("kubernetesJobUid")] string KubernetesJobUid,
    [property: JsonPropertyName("snapshotObjectName")] string SnapshotObjectName,
    [property: JsonPropertyName("snapshotSha256")] string SnapshotSha256,
    [property: JsonPropertyName("snapshotSize")] long SnapshotSize,
    [property: JsonPropertyName("snapshotDownloadUrl")] string SnapshotDownloadUrl,
    [property: JsonPropertyName("artifactBundleObjectName")] string ArtifactBundleObjectName,
    [property: JsonPropertyName("artifactBundleUploadUrl")] string ArtifactBundleUploadUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

internal interface IKubernetesObjectUrlSigner
{
    Task<string> SignSnapshotDownloadAsync(
        string objectName,
        int expirySeconds,
        CancellationToken cancellationToken);

    Task<string> SignArtifactBundleUploadAsync(
        string objectName,
        int expirySeconds,
        CancellationToken cancellationToken);
}

internal sealed class KubernetesObjectUrlSigner(
    IMinioClient minio,
    ArtifactStorageService artifactStorage) : IKubernetesObjectUrlSigner
{
    public Task<string> SignSnapshotDownloadAsync(
        string objectName,
        int expirySeconds,
        CancellationToken cancellationToken) =>
        minio.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(RepositorySnapshotStreamProvider.BucketName)
                .WithObject(objectName)
                .WithExpiry(expirySeconds));

    public async Task<string> SignArtifactBundleUploadAsync(
        string objectName,
        int expirySeconds,
        CancellationToken cancellationToken)
    {
        await artifactStorage.EnsureBucketAsync(cancellationToken);
        return await minio.PresignedPutObjectAsync(
            new PresignedPutObjectArgs()
                .WithBucket(ArtifactStorageService.BucketName)
                .WithObject(objectName)
                .WithExpiry(expirySeconds));
    }
}

internal static class KubernetesBuildTransportPolicy
{
    public const int CurrentVersion = 1;
    public const string SecretDataKey = "transport.json";
    private const int ExpiryBufferSeconds = 15 * 60;
    private const int MaximumPresignedExpirySeconds = 7 * 24 * 60 * 60;

    public static async Task<KubernetesBuildTransport> CreateAsync(
        BuildJob job,
        ProjectWebhookDelivery delivery,
        KubernetesBuildResourceIdentity identity,
        KubernetesJobLimits limits,
        IKubernetesObjectUrlSigner signer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ValidateInput(job, delivery, identity);
        var expirySeconds = checked((int)Math.Min(
            MaximumPresignedExpirySeconds,
            (long)limits.ActiveDeadlineSeconds + ExpiryBufferSeconds));
        var bundleObject = BundleObjectName(job.Id, identity.JobUid);
        var snapshotUrl = await signer.SignSnapshotDownloadAsync(
            delivery.SnapshotStoragePath!,
            expirySeconds,
            cancellationToken);
        var uploadUrl = await signer.SignArtifactBundleUploadAsync(
            bundleObject,
            expirySeconds,
            cancellationToken);
        ValidatePresignedUrl(snapshotUrl);
        ValidatePresignedUrl(uploadUrl);

        return new KubernetesBuildTransport(
            CurrentVersion,
            job.Id,
            identity.JobUid,
            delivery.SnapshotStoragePath!,
            delivery.SnapshotSha256!.ToLowerInvariant(),
            delivery.SnapshotFileSize!.Value,
            snapshotUrl,
            bundleObject,
            uploadUrl,
            now.AddSeconds(expirySeconds));
    }

    public static string BundleObjectName(Guid buildJobId, string jobUid)
    {
        _ = KubernetesArtifactManifestPolicy.ManifestObjectName(buildJobId, jobUid);
        return $"jobs/{buildJobId:N}/{jobUid}/bundle.tar";
    }

    public static V1Secret CreateSecret(
        string buildNamespace,
        string jobName,
        KubernetesBuildTransport transport)
    {
        ValidateTransportIdentity(jobName, transport);
        if (string.IsNullOrWhiteSpace(buildNamespace))
            throw new ValidationException("Kubernetes build namespace is required.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(transport);
        return new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Immutable = true,
            Type = "Opaque",
            Metadata = new V1ObjectMeta
            {
                Name = SecretName(jobName),
                NamespaceProperty = buildNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "lumina-ci",
                    ["lumina.1t.ru/build-job-id"] = transport.BuildJobId.ToString("N")
                },
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "batch/v1",
                        Kind = "Job",
                        Name = jobName,
                        Uid = transport.KubernetesJobUid,
                        Controller = true
                    }
                ]
            },
            Data = new Dictionary<string, byte[]> { [SecretDataKey] = bytes }
        };
    }

    public static string SecretName(string jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName) || jobName.Length > 52)
            throw new ValidationException("Kubernetes Job name cannot identify a transport Secret.");
        return $"{jobName}-transport";
    }

    private static void ValidateInput(
        BuildJob job,
        ProjectWebhookDelivery delivery,
        KubernetesBuildResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(identity);
        if (job.ExecutionBackend != BuildExecutorBackend.Kubernetes ||
            job.ProjectWebhookDeliveryId != delivery.Id ||
            job.ProjectWebhookDeliveryId == null ||
            delivery.BuildProjectId == Guid.Empty ||
            !string.Equals(identity.JobName, KubernetesBuildIdentity.JobName(job.Id), StringComparison.Ordinal) ||
            !string.Equals(identity.Namespace, job.KubernetesNamespace, StringComparison.Ordinal) ||
            !string.Equals(identity.JobUid, job.KubernetesJobUid, StringComparison.Ordinal) ||
            !string.Equals(delivery.CommitSha, job.CommitSha, StringComparison.Ordinal))
        {
            throw new ValidationException("Kubernetes build transport identity is invalid.");
        }
        if (string.IsNullOrWhiteSpace(delivery.SnapshotStoragePath) ||
            string.IsNullOrWhiteSpace(delivery.SnapshotSha256) ||
            !delivery.SnapshotFileSize.HasValue)
        {
            throw new ValidationException("Kubernetes build has no immutable repository snapshot.");
        }
        RepositorySnapshotStreamProvider.ValidateObjectIdentity(
            delivery.BuildProjectId,
            delivery.SnapshotStoragePath,
            delivery.SnapshotSha256,
            delivery.SnapshotFileSize.Value);
    }

    private static void ValidateTransportIdentity(
        string jobName,
        KubernetesBuildTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.Version != CurrentVersion ||
            transport.BuildJobId == Guid.Empty ||
            !string.Equals(jobName, KubernetesBuildIdentity.JobName(transport.BuildJobId), StringComparison.Ordinal) ||
            !string.Equals(
                transport.ArtifactBundleObjectName,
                BundleObjectName(transport.BuildJobId, transport.KubernetesJobUid),
                StringComparison.Ordinal) ||
            transport.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Kubernetes build transport document is invalid.");
        }
        ValidatePresignedUrl(transport.SnapshotDownloadUrl);
        ValidatePresignedUrl(transport.ArtifactBundleUploadUrl);
    }

    private static void ValidatePresignedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ValidationException("Kubernetes build transport URL is invalid.");
        }
    }
}
