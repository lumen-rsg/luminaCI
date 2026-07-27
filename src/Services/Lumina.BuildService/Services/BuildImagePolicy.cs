using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

/// <summary>
/// Resolves build images from an operator-managed allow-list. Pipeline authors
/// may select a configured runner, but cannot make BuildService pull and execute
/// an arbitrary image through the host Docker daemon.
/// </summary>
public static class BuildImagePolicy
{
    public const string DefaultImage = "lumina-rpm-build:latest";

    public static string Resolve(IConfiguration configuration, string? requestedImage)
    {
        var allowedImages = configuration
            .GetSection("Docker:AllowedBuildImages")
            .GetChildren()
            .Select(item => item.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        if (allowedImages.Count == 0)
        {
            throw new InvalidOperationException(
                "Docker:AllowedBuildImages must contain at least one administrator-managed runner image.");
        }

        var image = string.IsNullOrWhiteSpace(requestedImage)
            ? DefaultImage
            : requestedImage.Trim();

        if (!allowedImages.Contains(image))
        {
            throw new ValidationException(
                $"Build image '{image}' is not an allowed administrator-managed runner.");
        }

        return image;
    }
}
