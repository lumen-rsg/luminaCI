using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

public sealed record BuildTarget(
    string Distribution,
    string Release,
    string Architecture,
    string Profile);

/// <summary>
/// Defines the build targets for which Lumina has a reviewed runner. Keeping
/// this closed prevents a pipeline label from claiming a target that the
/// native container cannot actually produce.
/// </summary>
public static class BuildTargetPolicy
{
    private static readonly HashSet<string> SupportedArchitectures =
        new(StringComparer.Ordinal) { "x86_64", "aarch64" };

    public static BuildTarget Resolve(
        string? distribution,
        string? release,
        string? architecture,
        string? profile)
    {
        if (string.IsNullOrWhiteSpace(distribution) ||
            string.IsNullOrWhiteSpace(release) ||
            string.IsNullOrWhiteSpace(architecture) ||
            string.IsNullOrWhiteSpace(profile))
        {
            throw new ValidationException(
                "TargetDistribution, TargetRelease, TargetArchitecture, and BuildProfile are required.");
        }

        var normalizedDistribution = distribution.Trim().ToLowerInvariant();
        var normalizedRelease = release.Trim();
        var normalizedArchitecture = architecture.Trim().ToLowerInvariant();
        var normalizedProfile = profile.Trim().ToLowerInvariant();

        if (normalizedDistribution != "fedora" || normalizedRelease != "44")
        {
            throw new ValidationException(
                "Only the reviewed Fedora 44 build target is currently supported.");
        }

        if (!SupportedArchitectures.Contains(normalizedArchitecture))
        {
            throw new ValidationException(
                "TargetArchitecture must be either x86_64 or aarch64.");
        }

        var expectedProfile =
            $"{normalizedDistribution}-{normalizedRelease}-{normalizedArchitecture}";
        if (!string.Equals(normalizedProfile, expectedProfile, StringComparison.Ordinal))
        {
            throw new ValidationException(
                $"BuildProfile must be '{expectedProfile}' for the selected target.");
        }

        return new BuildTarget(
            normalizedDistribution,
            normalizedRelease,
            normalizedArchitecture,
            expectedProfile);
    }
}
