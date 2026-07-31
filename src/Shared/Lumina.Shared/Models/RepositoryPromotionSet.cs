using Lumina.Shared.Models.Enums;

namespace Lumina.Shared.Models;

public sealed class RepositoryPromotionSet
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public string PromotionGroup { get; set; } = string.Empty;
    public string TargetArchitecture { get; set; } = string.Empty;
    public string GateRunnerImageDigest { get; set; } = string.Empty;
    public PromotionSetStatus Status { get; set; } = PromotionSetStatus.Candidate;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? GateJobName { get; set; }
    public string? GateJobUid { get; set; }
    public string? GateResultSha256 { get; set; }
    public DateTime? GateCompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? PromotedAt { get; set; }
    public string? GateBundleObjectName { get; set; }
    public string? GateCandidateManifestSha256 { get; set; }
    public string? GateBundleSha256 { get; set; }
    public long? GateBundleSize { get; set; }
    public DateTime? GateBundlePreparedAt { get; set; }
    public string? GateBaselineManifestSha256 { get; set; }
    public string? PromotedRepositoryManifestSha256 { get; set; }
    public string? RollbackSnapshotPath { get; set; }
    public DateTime? RolledBackAt { get; set; }
    public string? RolledBackBy { get; set; }
    public string? RollbackReason { get; set; }

    public PackageRepository? Repository { get; set; }
    public List<Package> Packages { get; set; } = [];
}
