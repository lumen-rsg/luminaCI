using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.DTOs;

// Pipeline DTOs
public record CreatePipelineRequest(
    string Name,
    string Description,
    List<CreatePipelineStepRequest> Steps,
    List<string> Tags,
    string? GitRepoUrl = null,
    string? GitBranch = null,
    string? SpecPath = null,
    string? WebhookSecret = null,
    string? BuildImage = null,
    string? GitUsername = null,
    string? GitToken = null,
    string? SpecContent = null,
    string? TargetDistribution = null,
    string? TargetRelease = null,
    string? TargetArchitecture = null,
    string? BuildProfile = null);

public record UpdatePipelineRequest(
    string Name,
    string Description,
    List<CreatePipelineStepRequest> Steps,
    List<string> Tags,
    string? GitRepoUrl = null,
    string? GitBranch = null,
    string? SpecPath = null,
    string? BuildImage = null,
    string? GitUsername = null,
    string? GitToken = null,
    string? SpecContent = null,
    DateTime? ExpectedUpdatedAt = null,
    string? TargetDistribution = null,
    string? TargetRelease = null,
    string? TargetArchitecture = null,
    string? BuildProfile = null);

public record CreatePipelineStepRequest(StepType Type, string Name, int Order, Dictionary<string, string> Configuration);

// Build DTOs
public record TriggerBuildRequest(string SpecName, string SpecContent, string? SourceUrl, string TriggeredBy, string? CommitSha = null, string? Branch = null, string? CommitMessage = null, string? CommitAuthor = null, string? IdempotencyKey = null);

public record TriggerAutoBuildRequest(string TriggeredBy = "auto");

public record CancelBuildRequest(string Reason);

// Security DTOs
public record CreateKeyRequest(string KeyName, string PublicKey, string? PrivateKeyReference, DateTime? ExpiresAt, string CreatedBy);

public record GenerateKeyRequest(string KeyName, string Email);

public record VerifySignatureRequest(Guid ArtifactId, string Signature);

public record ComputeHashRequest(Guid ArtifactId, string FilePath = "");

public record StoreHashRequest(Guid ArtifactId, string FileName, string Sha256, string Md5, long FileSize);

// Scanner DTOs
public record ScanRequest(
    Guid ArtifactId,
    string? ArtifactPath = null,
    string ScannerType = "Trivy",
    string? ExpectedSha256 = null,
    long? ExpectedFileSize = null);

// Repository DTOs
public record CreateRepositoryRequest(string Name, string DisplayName, string BasePath, string Arch, string Distribution, string CreatedBy);

public record PublishPackageRequest(Guid ArtifactId, Guid RepositoryId, string PublishedBy);

public record SyncRepositoryRequest(Guid RepositoryId);

// Source DTOs
public record SavePackageSourceRequest(
    string Slug,
    string SourceUrl,
    SourceType SourceType,
    string? SourceReference = null,
    string? ExpectedSha256 = null,
    string? SpecPath = null,
    string? BuildImage = null,
    bool IsEnabled = true,
    bool FetchAutomatically = true,
    int? ExpectedRevision = null);

public record FetchSourceOptionsRequest(int MaxRetries = 3);

public record FetchAllSourcesRequest(int MaxRetries = 3);
