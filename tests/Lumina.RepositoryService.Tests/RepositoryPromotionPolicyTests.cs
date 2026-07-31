using Lumina.RepositoryService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.RepositoryService.Tests;

public sealed class RepositoryPromotionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string Digest = new('a', 64);

    [Fact]
    public void Candidate_CanOnlyPromoteAfterSuccessfulNativeGate()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now.AddMinutes(1));

        Assert.Equal("Candidate", package.Status);
        Assert.Equal(set.Id, package.PromotionSetId);
        Assert.Throws<ConflictException>(() =>
            RepositoryPromotionPolicy.MarkPromoted(set, Digest, "/snapshots/test", Now.AddMinutes(2)));

        Prepare(set);
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now.AddMinutes(2));
        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now.AddMinutes(3));
        package.Status = "Ready";
        RepositoryPromotionPolicy.MarkPromoted(
            set, Digest, "/snapshots/test", Now.AddMinutes(4));

        Assert.Equal(PromotionSetStatus.Promoted, set.Status);
        Assert.Equal(Now.AddMinutes(3), set.GateCompletedAt);
        Assert.Equal(Now.AddMinutes(4), set.PromotedAt);
    }

    [Fact]
    public void FailedGate_IsTerminalAndRecordsBoundedReason()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now);
        Prepare(set);
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now);

        RepositoryPromotionPolicy.RecordGateFailure(set, "dnf transaction failed", Now);

        Assert.Equal(PromotionSetStatus.Failed, set.Status);
        Assert.Equal("dnf transaction failed", set.FailureReason);
        Assert.Throws<ConflictException>(() =>
            RepositoryPromotionPolicy.MarkPromoted(set, Digest, "/snapshots/test", Now));
    }

    [Fact]
    public void FailedBundlePreparation_IsTerminalWithoutKubernetesIdentity()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now);

        RepositoryPromotionPolicy.RecordGatePreparationFailure(set, "snapshot failed", Now);
        RepositoryPromotionPolicy.RecordGatePreparationFailure(set, "snapshot failed", Now.AddMinutes(1));

        Assert.Equal(PromotionSetStatus.Failed, set.Status);
        Assert.Null(set.GateJobName);
        Assert.Equal("snapshot failed", set.FailureReason);
        Assert.Throws<ConflictException>(() => RepositoryPromotionPolicy.RecordGatePreparationFailure(
            set, "changed", Now));
    }

    [Fact]
    public void GateAcknowledgements_AreIdempotentButRejectChangedIdentityOrResult()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now);
        Prepare(set);
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now);
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now.AddSeconds(1));
        Assert.Throws<ConflictException>(() => RepositoryPromotionPolicy.BeginGate(
            set, "lumina-gate-changed", "job-uid-2", Now));

        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now.AddMinutes(1));
        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now.AddMinutes(2));
        Assert.Throws<ConflictException>(() => RepositoryPromotionPolicy.RecordGateSuccess(
            set, new string('b', 64), Now.AddMinutes(2)));
    }

    [Fact]
    public void GateBundle_IsContentAddressedAndRetryStable()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now);
        Prepare(set);
        Prepare(set);

        Assert.Equal(new string('c', 64), set.GateCandidateManifestSha256);
        Assert.Equal(new string('b', 64), set.GateBaselineManifestSha256);
        Assert.Throws<ConflictException>(() => RepositoryPromotionPolicy.RecordGateBundle(
            set, new string('c', 64), new string('b', 64),
            $"promotion-gates/{set.Id:N}/sha256/{new string('e', 64)}/input.tar",
            new string('e', 64), 4096, Now));
    }

    [Fact]
    public void PromotedSet_RetainsExactRollbackIdentityAndAttribution()
    {
        var set = CreateSet();
        var package = Candidate(set.RepositoryId);
        RepositoryPromotionPolicy.AttachCandidate(
            set, package, $"sha256/{Digest}/{package.FileName}", Now);
        Prepare(set);
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now);
        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now);
        package.Status = "Ready";
        RepositoryPromotionPolicy.MarkPromoted(set, Digest, "/snapshots/exact", Now);
        RepositoryPromotionPolicy.MarkPromoted(set, Digest, "/snapshots/exact", Now.AddMinutes(1));

        package.Status = "RolledBack";
        RepositoryPromotionPolicy.MarkRolledBack(set, "cv2", "release regression", Now.AddMinutes(2));
        RepositoryPromotionPolicy.MarkRolledBack(set, "cv2", "release regression", Now.AddMinutes(3));
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now.AddMinutes(4));
        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now.AddMinutes(4));

        Assert.Equal(PromotionSetStatus.RolledBack, set.Status);
        Assert.Null(set.RollbackSnapshotPath);
        Assert.Equal("cv2", set.RolledBackBy);
        Assert.Throws<ConflictException>(() => RepositoryPromotionPolicy.MarkRolledBack(
            set, "dprog", "changed", Now));
    }

    [Fact]
    public void Candidate_RejectsWrongArchitectureAndObjectIdentity()
    {
        var set = CreateSet();
        var wrongArch = Candidate(set.RepositoryId);
        wrongArch.Arch = "x86_64";
        Assert.Throws<ValidationException>(() =>
            RepositoryPromotionPolicy.AttachCandidate(
                set, wrongArch, $"sha256/{Digest}/{wrongArch.FileName}", Now));

        var package = Candidate(set.RepositoryId);
        Assert.Throws<ValidationException>(() =>
            RepositoryPromotionPolicy.AttachCandidate(
                set, package, $"sha256/{new string('b', 64)}/{package.FileName}", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Kernel Tegra")]
    [InlineData("../kernel")]
    public void PackageMembership_RejectsUnsafeIdentity(string packageId)
    {
        Assert.Throws<ValidationException>(() =>
            RepositoryPromotionPolicy.NormalizePackageId(packageId));
    }

    private static RepositoryPromotionSet CreateSet() =>
        RepositoryPromotionPolicy.Create(
            Guid.NewGuid(), Guid.NewGuid(), "jetson-r39.2", "aarch64",
            $"sha256:{Digest}", "cv2", Now);

    private static void Prepare(RepositoryPromotionSet set) =>
        RepositoryPromotionPolicy.RecordGateBundle(
            set,
            new string('c', 64),
            new string('b', 64),
            $"promotion-gates/{set.Id:N}/sha256/{new string('d', 64)}/input.tar",
            new string('d', 64),
            4096,
            Now);

    private static Package Candidate(Guid repositoryId) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        ArtifactId = Guid.NewGuid(),
        PromotionPackageId = "tegra-firmware",
        Name = "tegra-firmware",
        Version = "39.2",
        Release = "1.lu26",
        Arch = "aarch64",
        FileName = "tegra-firmware-39.2-1.lu26.aarch64.rpm",
        StoragePath = string.Empty,
        FileSize = 42,
        HashSha256 = Digest,
        SigningKeyFingerprint = "ABC123",
        PublishedBy = "cv2"
    };
}
