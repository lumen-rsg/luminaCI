using Lumina.RepositoryService.Services;
using Lumina.Shared.Models;
using Xunit;

namespace Lumina.RepositoryService.Tests;

public sealed class CandidateIdentityPolicyTests
{
    private readonly Guid _repositoryId = Guid.NewGuid();
    private readonly Guid _promotionSetId = Guid.NewGuid();

    [Fact]
    public void ReadyPackageWithSameNevra_BlocksCandidate()
    {
        var candidate = Package();
        var existing = Package(status: "Ready");

        Assert.True(Conflicts(candidate, existing));
    }

    [Fact]
    public void SameSetCandidateWithSameFileName_BlocksCandidate()
    {
        var candidate = Package();
        var existing = Package(
            status: "Candidate",
            promotionSetId: _promotionSetId,
            name: "different-name");

        Assert.True(Conflicts(candidate, existing));
    }

    [Fact]
    public void CandidateFromFailedDelivery_DoesNotBlockRetrySet()
    {
        var candidate = Package();
        var existing = Package(
            status: "Candidate",
            promotionSetId: Guid.NewGuid());

        Assert.False(Conflicts(candidate, existing));
    }

    [Fact]
    public void PackageInAnotherRepository_DoesNotBlockCandidate()
    {
        var candidate = Package();
        var existing = Package(
            status: "Ready",
            repositoryId: Guid.NewGuid());

        Assert.False(Conflicts(candidate, existing));
    }

    private bool Conflicts(Package candidate, Package existing) =>
        CandidateIdentityPolicy.Conflicts(_repositoryId, _promotionSetId, candidate)
            .Compile()(existing);

    private Package Package(
        string status = "Candidate",
        Guid? promotionSetId = null,
        Guid? repositoryId = null,
        string name = "package") => new()
    {
        RepositoryId = repositoryId ?? _repositoryId,
        PromotionSetId = promotionSetId,
        Name = name,
        Version = "1.0",
        Release = "1.lu26",
        Arch = "noarch",
        FileName = "package-1.0-1.lu26.noarch.rpm",
        Status = status
    };
}
