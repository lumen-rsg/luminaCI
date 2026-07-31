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
                    PromotionSetStatus.Failed or PromotionSetStatus.Promoted &&
                string.Equals(set.GateJobName, jobName, StringComparison.Ordinal) &&
                string.Equals(set.GateJobUid, jobUid, StringComparison.Ordinal))
            {
                return;
            }
            RequireStatus(set, PromotionSetStatus.Candidate);
        }
        if (set.Packages.Count == 0)
            throw new ValidationException("A promotion set cannot be tested without candidate packages.");

        set.Status = PromotionSetStatus.Testing;
        set.GateJobName = jobName;
        set.GateJobUid = jobUid;
        set.UpdatedAt = now;
    }

    public static void RecordGateSuccess(
        RepositoryPromotionSet set,
        string resultSha256,
        DateTime now)
    {
        var normalized = NormalizeSha256(resultSha256, "Gate result SHA-256");
        if (set.Status is PromotionSetStatus.Passed or PromotionSetStatus.Promoted &&
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
        var reason = (failureReason ?? string.Empty).Trim();
        if (reason.Length is < 1 or > 2048)
            throw new ValidationException("Native gate failure reason is invalid.");
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

    public static void MarkPromoted(RepositoryPromotionSet set, DateTime now)
    {
        RequireStatus(set, PromotionSetStatus.Passed);
        if (set.Packages.Count == 0 || set.Packages.Any(package => package.Status != "Ready"))
            throw new ValidationException(
                "Every candidate package must be atomically committed before the promotion set is marked promoted.");
        set.Status = PromotionSetStatus.Promoted;
        set.PromotedAt = now;
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
