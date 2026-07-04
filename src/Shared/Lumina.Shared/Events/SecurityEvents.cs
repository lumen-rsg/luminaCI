namespace Lumina.Shared.Events;

// Note: PackageSigningRequested and PackageSigned are defined in BuildEvents.cs

public record SignatureVerificationRequested(Guid ArtifactId, string FileName, string StoragePath, string Signature, DateTime RequestedAt);

public record SignatureVerified(Guid ArtifactId, bool IsValid, DateTime VerifiedAt);