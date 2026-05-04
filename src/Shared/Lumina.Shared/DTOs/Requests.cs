using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.DTOs;

// Pipeline DTOs
public record CreatePipelineRequest(string Name, string Description, List<CreatePipelineStepRequest> Steps, List<string> Tags, string? GitRepoUrl = null, string? GitBranch = null, string? SpecPath = null, string? WebhookSecret = null, string? BuildImage = null);

public record UpdatePipelineRequest(string Name, string Description, List<CreatePipelineStepRequest> Steps, List<string> Tags);

public record CreatePipelineStepRequest(StepType Type, string Name, int Order, Dictionary<string, string> Configuration);

// Build DTOs
public record TriggerBuildRequest(string SpecName, string SpecContent, string? SourceUrl, string TriggeredBy);

public record CancelBuildRequest(string Reason);

// Security DTOs
public record CreateKeyRequest(string KeyName, string PublicKey, string? PrivateKeyReference, DateTime? ExpiresAt, string CreatedBy);

public record GenerateKeyRequest(string KeyName, string Email, string Passphrase);

public record SignPackageRequest(Guid ArtifactId, Guid KeyId);

public record SignArtifactRequest(Guid ArtifactId, string ArtifactPath, Guid KeyId);

public record VerifySignatureRequest(Guid ArtifactId, string Signature);

public record ComputeHashRequest(Guid ArtifactId, string FilePath = "");

// Scanner DTOs
public record ScanRequest(Guid ArtifactId, string ScannerType = "Trivy");

// Repository DTOs
public record CreateRepositoryRequest(string Name, string DisplayName, string BasePath, string Arch, string Distribution, string CreatedBy);

public record PublishPackageRequest(Guid ArtifactId, Guid RepositoryId, string PublishedBy);

public record SyncRepositoryRequest(Guid RepositoryId);