using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public sealed record CandidatePromotionSelection(bool Enabled)
{
    public static CandidatePromotionSelection Resolve(
        IConfiguration configuration,
        BuildExecutorBackend executorBackend)
    {
        var enabled = configuration.GetValue("RepositoryPromotion:Enabled", false);
        if (enabled && executorBackend != BuildExecutorBackend.Kubernetes)
        {
            throw new InvalidOperationException(
                "Candidate promotion requires the Kubernetes executor for native transaction gates.");
        }
        return new CandidatePromotionSelection(enabled);
    }
}
