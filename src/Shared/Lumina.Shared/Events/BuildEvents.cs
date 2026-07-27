using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Events;

// Build Events
public record BuildJobCreated(Guid BuildJobId, Guid PipelineId, string SpecName, string TriggeredBy, DateTime CreatedAt);

public record BuildJobStarted(Guid BuildJobId, string ContainerId, DateTime StartedAt);

public record BuildJobCompleted(Guid BuildJobId, BuildStatus Status, DateTime CompletedAt);

public record BuildJobFailed(Guid BuildJobId, string ErrorMessage, DateTime FailedAt);

/// <summary>
/// Sent by BuildService after a successful build to request CVE scanning of artifacts.
/// Consumed by ScannerService.
/// </summary>
public record CveScanRequested(Guid ArtifactId, string ArtifactPath, string FileName, string ScannerType, DateTime RequestedAt);

/// <summary>
/// Sent by ScannerService when CVE scan completes.
/// Consumed by BuildService to update artifact scan status.
/// </summary>
/// <remarks>
/// <see cref="UnknownCount"/> counts vulnerabilities whose severity could not
/// be classified (Trivy returned an unrecognized or empty value). BuildService
/// treats unknowns conservatively and blocks signing when it is non-zero, so a
/// parser/labeling failure cannot masquerade as a clean scan.
/// </remarks>
public record CveScanCompleted(Guid ArtifactId, ScanStatus Status, int CriticalCount, int HighCount, int MediumCount, int LowCount, int UnknownCount, DateTime CompletedAt);

/// <summary>
/// Sent by BuildService to request hash storage for an artifact.
/// Consumed by SecurityService.
/// </summary>
public record HashStoreRequested(Guid ArtifactId, string FileName, string Sha256, string Md5, long FileSize, DateTime RequestedAt);

/// <summary>
/// Sent by BuildService to request PGP signing of an artifact.
/// Consumed by SecurityService.
/// </summary>
public record PackageSigningRequested(Guid ArtifactId, string ArtifactPath, string FileName, Guid KeyId, DateTime RequestedAt);

/// <summary>
/// Sent by SecurityService when PGP signing completes.
/// Consumed by BuildService to update artifact signature.
/// </summary>
public record PackageSigned(Guid ArtifactId, string PgpSignature, DateTime SignedAt);

/// <summary>
/// Request the currently-active PGP key from SecurityService over the message
/// bus. This replaces the previous direct HTTP call
/// (<c>GET /api/security/keys</c>) that BuildService made to SecurityService,
/// which carried no JWT and would now be rejected once SecurityController is
/// gated by <c>[Authorize]</c>. MassTransit request/response keeps the lookup on
/// the trusted bus — no token, no exposed HTTP surface.
/// </summary>
public record GetActiveSigningKey();

/// <summary>
/// Response to <see cref="GetActiveSigningKey"/>. <c>KeyId</c> is null when no
/// active PGP key exists.
/// </summary>
public record ActiveSigningKey(Guid? KeyId);

/// <summary>
/// Request the stored PGP signature for a build artifact. RepositoryService
/// uses this at publish time to enforce the "no unsigned publication" gate —
/// it has no view of BuildDbContext, so it asks BuildService over the bus.
/// </summary>
public record GetArtifactSignature(Guid ArtifactId);

/// <summary>
/// Response to <see cref="GetArtifactSignature"/>. <c>PgpSignature</c> is null
/// when the artifact has not been signed (no active key, signing failed, or the
/// CVE scan skipped signing due to vulnerabilities).
/// </summary>
public record ArtifactSignature(string? PgpSignature);

/// <summary>
/// Request the content + real NEVRA metadata of a build artifact over the
/// message bus. RepositoryService uses this at publish time to obtain the RPM
/// bytes and the real filename/arch — it has no view of BuildDbContext and no
/// shared filesystem with BuildService, so the artifact must travel over the
/// bus. <see cref="GetArtifactSignature"/> is the tiny sibling of this request.
/// </summary>
public record GetArtifactContent(Guid ArtifactId);

/// <summary>
/// Response to <see cref="GetArtifactContent"/>.
/// <para><c>FileName</c> is the real RPM filename (NEVRA, e.g.
/// <c>foo-1.0-1.x86_64.rpm</c>) recorded by <c>ScanArtifactsAsync</c>; the
/// caller should treat it as untrusted and basename it before any filesystem
/// use.</para>
/// <para><c>Content</c> carries the full RPM bytes; <c>HashSha256</c> and
/// <c>FileSize</c> are the values BuildService already computed at scan time,
/// so the publisher can record them without re-hashing.</para>
/// </summary>
public record ArtifactContent(string FileName, long FileSize, string? HashSha256, byte[] Content);

/// <summary>
/// Request the armored public key of the active PGP key. RepositoryService uses
/// this to verify externally-uploaded RPM signatures with <c>gpg --verify</c>.
/// It runs in its own container with its own (transient) keyring and has no
/// shared filesystem with SecurityService, so it fetches the key over the bus.
/// </summary>
public record GetActivePublicKey();

/// <summary>
/// Response to <see cref="GetActivePublicKey"/>. <c>PublicKeyArmored</c> is null
/// when no active PGP key exists.
/// </summary>
public record ActivePublicKey(string? PublicKeyArmored);
