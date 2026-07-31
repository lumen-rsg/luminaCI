using System.Text.RegularExpressions;
using Lumina.Shared.Errors;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;

namespace Lumina.RepositoryService.Services;

public static partial class RepositoryPromotionPolicy
{
    public static RepositoryPromotionSet Create(
        Guid id,
        Guid repositoryId,
        string promotionGroup,
        string targetArchitecture,
        string gateRunnerImageDigest,
        string createdBy,
        DateTime now)
    {
        if (id == Guid.Empty || repositoryId == Guid.Empty)
            throw new ValidationException("Promotion-set identity is invalid.");
        var group = PromotionSetIdentity.NormalizeGroup(promotionGroup);
        ProcessArgumentSanitizer.ValidateRepositoryArch(targetArchitecture);
        var digest = NormalizeSha256(gateRunnerImageDigest, "Gate runner image digest");
        var actor = (createdBy ?? string.Empty).Trim();
        if (actor.Length is < 1 or > 256)
            throw new ValidationException("Promotion-set creator is invalid.");

        return new RepositoryPromotionSet
        {
            Id = id,
            RepositoryId = repositoryId,
            PromotionGroup = group,
            TargetArchitecture = targetArchitecture,
            GateRunnerImageDigest = $"sha256:{digest}",
            Status = PromotionSetStatus.Candidate,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static string NormalizePackageId(string packageId)
    {
        var normalized = (packageId ?? string.Empty).Trim();
        if (!PackageIdPattern().IsMatch(normalized))
            throw new ValidationException("Promotion package ID is invalid.");
        return normalized;
    }

    public static void AttachCandidate(
        RepositoryPromotionSet set,
        Package package,
        string candidateObjectName,
        DateTime now)
    {
        RequireStatus(set, PromotionSetStatus.Candidate);
        if (package.RepositoryId != set.RepositoryId || package.ArtifactId is null ||
            string.IsNullOrWhiteSpace(package.SigningKeyFingerprint) ||
            !PackageIdPattern().IsMatch(package.PromotionPackageId ?? string.Empty))
        {
            throw new ValidationException("Candidate package provenance is incomplete.");
        }
        if (!string.Equals(package.Arch, set.TargetArchitecture, StringComparison.Ordinal) &&
            !string.Equals(package.Arch, "noarch", StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"Candidate architecture '{package.Arch}' does not match target '{set.TargetArchitecture}'.");
        }

        var hash = NormalizeSha256(package.HashSha256, "Candidate SHA-256");
        var expectedObjectName = $"sha256/{hash}/{package.FileName}";
        if (!string.Equals(candidateObjectName, expectedObjectName, StringComparison.Ordinal))
            throw new ValidationException("Candidate object is not the exact content-addressed RPM.");
        if (set.Packages.Any(existing =>
                existing.ArtifactId == package.ArtifactId ||
                string.Equals(existing.FileName, package.FileName, StringComparison.Ordinal) ||
                (string.Equals(existing.Name, package.Name, StringComparison.Ordinal) &&
                 string.Equals(existing.Version, package.Version, StringComparison.Ordinal) &&
                 string.Equals(existing.Release, package.Release, StringComparison.Ordinal) &&
                 string.Equals(existing.Arch, package.Arch, StringComparison.Ordinal))))
        {
            throw new ConflictException("Candidate package is already present in this promotion set.");
        }

        package.PromotionSetId = set.Id;
        package.PromotionSet = set;
        package.CandidateObjectName = candidateObjectName;
        package.Status = "Candidate";
        set.Packages.Add(package);
        set.UpdatedAt = now;
    }

    public static void BeginGate(
        RepositoryPromotionSet set,
        string jobName,
        string jobUid,
        DateTime now)
    {
        if (!KubernetesNamePattern().IsMatch(jobName ?? string.Empty) ||
            string.IsNullOrWhiteSpace(jobUid) || jobUid.Length > 128)
        {
            throw new ValidationException("Native gate Job identity is invalid.");
        }
        if (set.Status != PromotionSetStatus.Candidate)
        {
            if (set.Status is PromotionSetStatus.Testing or PromotionSetStatus.Passed or
                    PromotionSetStatus.Failed or PromotionSetStatus.Promoted or PromotionSetStatus.RolledBack &&
                string.Equals(set.GateJobName, jobName, StringComparison.Ordinal) &&
                string.Equals(set.GateJobUid, jobUid, StringComparison.Ordinal))
            {
                return;
            }
            RequireStatus(set, PromotionSetStatus.Candidate);
        }
        if (set.Packages.Count == 0)
            throw new ValidationException("A promotion set cannot be tested without candidate packages.");
        if (string.IsNullOrWhiteSpace(set.GateBundleObjectName) ||
            string.IsNullOrWhiteSpace(set.GateCandidateManifestSha256) ||
            string.IsNullOrWhiteSpace(set.GateBundleSha256) ||
            set.GateBundleSize is null or <= 0 || set.GateBundlePreparedAt is null)
            throw new ValidationException("A promotion set cannot be tested without an immutable gate bundle.");

        set.Status = PromotionSetStatus.Testing;
        set.GateJobName = jobName;
        set.GateJobUid = jobUid;
        set.UpdatedAt = now;
    }

    public static void RecordGateBundle(
        RepositoryPromotionSet set,
        string candidateManifestSha256,
        string baselineManifestSha256,
        string bundleObjectName,
        string bundleSha256,
        long bundleSize,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(set);
        var manifestHash = NormalizeSha256(candidateManifestSha256, "Candidate manifest SHA-256");
        var baselineHash = NormalizeSha256(baselineManifestSha256, "Baseline manifest SHA-256");
        var bundleHash = NormalizeSha256(bundleSha256, "Gate bundle SHA-256");
        var expectedObject = $"promotion-gates/{set.Id:N}/sha256/{bundleHash}/input.tar";
        if (!string.Equals(bundleObjectName, expectedObject, StringComparison.Ordinal) ||
            bundleSize is <= 0 or > 8L * 1024 * 1024 * 1024)
            throw new ValidationException("Gate bundle object identity is invalid.");
        if (set.GateBundleObjectName is not null)
        {
            if (set.GateCandidateManifestSha256 == manifestHash &&
                set.GateBaselineManifestSha256 == baselineHash &&
                set.GateBundleObjectName == expectedObject && set.GateBundleSha256 == bundleHash &&
                set.GateBundleSize == bundleSize)
                return;
            throw new ConflictException("Promotion gate bundle identity changed after preparation.");
        }
        RequireStatus(set, PromotionSetStatus.Candidate);
        if (set.Packages.Count == 0)
            throw new ValidationException("A promotion gate bundle requires candidate packages.");
        set.GateCandidateManifestSha256 = manifestHash;
        set.GateBaselineManifestSha256 = baselineHash;
        set.GateBundleObjectName = expectedObject;
        set.GateBundleSha256 = bundleHash;
        set.GateBundleSize = bundleSize;
        set.GateBundlePreparedAt = now;
        set.UpdatedAt = now;
    }

    public static void RecordGateSuccess(
        RepositoryPromotionSet set,
        string resultSha256,
        DateTime now)
    {
        var normalized = NormalizeSha256(resultSha256, "Gate result SHA-256");
        if (set.Status is PromotionSetStatus.Passed or PromotionSetStatus.Promoted or PromotionSetStatus.RolledBack &&
            string.Equals(set.GateResultSha256, normalized, StringComparison.Ordinal))
        {
            return;
        }
        RequireStatus(set, PromotionSetStatus.Testing);
        set.Status = PromotionSetStatus.Passed;
        set.GateResultSha256 = normalized;
        set.GateCompletedAt = now;
        set.UpdatedAt = now;
    }

    public static void RecordGateFailure(
        RepositoryPromotionSet set,
        string failureReason,
        DateTime now)
    {
        var reason = NormalizeFailureReason(failureReason);
        if (set.Status == PromotionSetStatus.Failed &&
            string.Equals(set.FailureReason, reason, StringComparison.Ordinal))
        {
            return;
        }
        RequireStatus(set, PromotionSetStatus.Testing);
        set.Status = PromotionSetStatus.Failed;
        set.FailureReason = reason;
        set.GateCompletedAt = now;
        set.UpdatedAt = now;
    }

    public static void RecordGatePreparationFailure(
        RepositoryPromotionSet set,
        string failureReason,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(set);
        var reason = NormalizeFailureReason(failureReason);
        if (set.Status == PromotionSetStatus.Failed && set.GateJobName is null)
        {
            if (string.Equals(set.FailureReason, reason, StringComparison.Ordinal))
                return;
            throw new ConflictException("Promotion gate preparation failure changed after recording.");
        }
        RequireStatus(set, PromotionSetStatus.Candidate);
        set.Status = PromotionSetStatus.Failed;
        set.FailureReason = reason;
        set.GateCompletedAt = now;
        set.UpdatedAt = now;
    }

    public static void MarkPromoted(
        RepositoryPromotionSet set,
        string repositoryManifestSha256,
        string rollbackSnapshotPath,
        DateTime now)
    {
        var manifestHash = NormalizeSha256(
            repositoryManifestSha256, "Promoted repository manifest SHA-256");
        if (string.IsNullOrWhiteSpace(rollbackSnapshotPath) || rollbackSnapshotPath.Length > 1024 ||
            rollbackSnapshotPath.IndexOf('\0') >= 0)
            throw new ValidationException("Rollback snapshot path is invalid.");
        if (set.Status == PromotionSetStatus.Promoted)
        {
            if (set.PromotedRepositoryManifestSha256 == manifestHash &&
                set.RollbackSnapshotPath == rollbackSnapshotPath)
                return;
            throw new ConflictException("Promotion repository identity changed after commit.");
        }
        RequireStatus(set, PromotionSetStatus.Passed);
        if (set.Packages.Count == 0 || set.Packages.Any(package => package.Status != "Ready"))
            throw new ValidationException(
                "Every candidate package must be atomically committed before the promotion set is marked promoted.");
        set.Status = PromotionSetStatus.Promoted;
        set.PromotedRepositoryManifestSha256 = manifestHash;
        set.RollbackSnapshotPath = rollbackSnapshotPath;
        set.PromotedAt = now;
        set.UpdatedAt = now;
    }

    public static void MarkRolledBack(
        RepositoryPromotionSet set,
        string rolledBackBy,
        string reason,
        DateTime now)
    {
        var actor = (rolledBackBy ?? string.Empty).Trim();
        if (actor.Length is < 1 or > 256)
            throw new ValidationException("Rollback actor is invalid.");
        var failure = NormalizeFailureReason(reason);
        if (set.Status == PromotionSetStatus.RolledBack)
        {
            if (set.RolledBackBy == actor && set.RollbackReason == failure)
                return;
            throw new ConflictException("Rollback attribution changed after commit.");
        }
        RequireStatus(set, PromotionSetStatus.Promoted);
        if (set.Packages.Count == 0 || set.Packages.Any(package => package.Status != "RolledBack"))
            throw new ValidationException("Every promoted package must be restored before rollback is recorded.");
        set.Status = PromotionSetStatus.RolledBack;
        set.RollbackSnapshotPath = null;
        set.RolledBackAt = now;
        set.RolledBackBy = actor;
        set.RollbackReason = failure;
        set.UpdatedAt = now;
    }

    private static string NormalizeSha256(string? value, string field)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal))
            normalized = normalized[7..];
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ValidationException($"{field} must be a full SHA-256 digest.");
        return normalized;
    }

    private static string NormalizeFailureReason(string? value)
    {
        var reason = (value ?? string.Empty).Trim();
        if (reason.Length is < 1 or > 2048)
            throw new ValidationException("Native gate failure reason is invalid.");
        return reason;
    }

    private static void RequireStatus(RepositoryPromotionSet set, PromotionSetStatus expected)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Status != expected)
            throw new ConflictException(
                $"Promotion set {set.Id} is {set.Status} and cannot transition from {expected}.");
    }

    [GeneratedRegex("^[a-z0-9](?:[-a-z0-9]{0,61}[a-z0-9])?$")]
    private static partial Regex KubernetesNamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex PackageIdPattern();
}
