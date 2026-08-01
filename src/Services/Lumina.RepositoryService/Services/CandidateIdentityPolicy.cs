using System.Linq.Expressions;
using Lumina.Shared.Models;

namespace Lumina.RepositoryService.Services;

internal static class CandidateIdentityPolicy
{
    public static Expression<Func<Package, bool>> Conflicts(
        Guid repositoryId,
        Guid promotionSetId,
        Package candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return package =>
            package.RepositoryId == repositoryId &&
            (package.Status == "Ready" || package.PromotionSetId == promotionSetId) &&
            (package.FileName == candidate.FileName ||
             (package.Name == candidate.Name &&
              package.Version == candidate.Version &&
              package.Release == candidate.Release &&
              package.Arch == candidate.Arch));
    }
}
