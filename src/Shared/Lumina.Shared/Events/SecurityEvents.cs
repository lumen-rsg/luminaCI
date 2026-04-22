namespace Lumina.Shared.Events;

// Security Events
public record PackageSigningRequested(Guid ArtifactId, string FileName, string StoragePath, Guid KeyId, DateTime RequestedAt);

public record PackageSigned(Guid ArtifactId, string PgpSignature, DateTime SignedAt);

public record HashComputed(Guid ArtifactId, string HashSha256, string HashSha512, string HashMd5, DateTime ComputedAt);

public record SignatureVerificationRequested(Guid ArtifactId, string FileName, string StoragePath, string Signature, DateTime RequestedAt);

public record SignatureVerified(Guid ArtifactId, bool IsValid, DateTime VerifiedAt);