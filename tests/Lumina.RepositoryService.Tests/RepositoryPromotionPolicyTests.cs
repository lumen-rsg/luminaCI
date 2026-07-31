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
            RepositoryPromotionPolicy.MarkPromoted(set, Now.AddMinutes(2)));

        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now.AddMinutes(2));
        RepositoryPromotionPolicy.RecordGateSuccess(set, Digest, Now.AddMinutes(3));
        package.Status = "Ready";
        RepositoryPromotionPolicy.MarkPromoted(set, Now.AddMinutes(4));

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
        RepositoryPromotionPolicy.BeginGate(set, "lumina-gate-123", "job-uid-1", Now);

        RepositoryPromotionPolicy.RecordGateFailure(set, "dnf transaction failed", Now);

        Assert.Equal(PromotionSetStatus.Failed, set.Status);
        Assert.Equal("dnf transaction failed", set.FailureReason);
        Assert.Throws<ConflictException>(() =>
            RepositoryPromotionPolicy.MarkPromoted(set, Now));
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
