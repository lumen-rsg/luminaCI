using Lumina.Shared.Models;
using Lumina.Shared.Errors;
using Xunit;

namespace Lumina.Shared.Tests;

public sealed class PromotionSetIdentityTests
{
    [Fact]
    public void Create_IsStableAndSeparatesOwnersAndGroups()
    {
        var owner = Guid.Parse("2f5581e3-d606-4fea-abf6-1a19f2ad5b70");

        var first = PromotionSetIdentity.Create(owner, "jetson-r39.2");

        Assert.Equal(first, PromotionSetIdentity.Create(owner, "jetson-r39.2"));
        Assert.NotEqual(first, PromotionSetIdentity.Create(owner, "jetson-r39.3"));
        Assert.NotEqual(first, PromotionSetIdentity.Create(Guid.NewGuid(), "jetson-r39.2"));
        Assert.Equal(8, first.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("../escape")]
    public void Create_RejectsUnsafeGroups(string group)
    {
        Assert.Throws<ValidationException>(() =>
            PromotionSetIdentity.Create(Guid.NewGuid(), group));
    }
}
