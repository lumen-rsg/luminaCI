using System.Text.RegularExpressions;
using Lumina.Shared.Models.Enums;

namespace Lumina.BuildService.Services;

public sealed record BuildExecutorSelection(
    BuildExecutorBackend Backend,
    string? KubernetesNamespace);

/// <summary>
/// Makes the global executor choice explicit. Kubernetes cannot be selected
/// until the caller confirms that its complete durable transport is present;
/// this prevents a configuration typo from silently falling back to Docker.
/// </summary>
public static partial class BuildExecutorSelectionPolicy
{
    public static BuildExecutorSelection Resolve(
        IConfiguration configuration,
        bool kubernetesTransportAvailable)
    {
        var value = configuration["BuildExecutor:Type"]?.Trim() ?? "Docker";
        if (string.Equals(value, "Docker", StringComparison.OrdinalIgnoreCase))
            return new BuildExecutorSelection(BuildExecutorBackend.Docker, null);
        if (!string.Equals(value, "Kubernetes", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BuildExecutor:Type must be either Docker or Kubernetes.");

        if (!bool.TryParse(configuration["Kubernetes:Enabled"], out var enabled) || !enabled)
            throw new InvalidOperationException("Kubernetes:Enabled must be true before selecting the Kubernetes executor.");
        if (!kubernetesTransportAvailable)
        {
            throw new InvalidOperationException(
                "The Kubernetes executor cannot be selected until durable create, monitor, cancellation, and recovery transport is enabled.");
        }
        var buildNamespace = configuration["Kubernetes:Namespace"]?.Trim() ?? string.Empty;
        if (!NamespacePattern().IsMatch(buildNamespace))
            throw new InvalidOperationException("Kubernetes:Namespace must be a valid DNS label.");
        KubernetesBuildPolicy.ValidateConfiguration(configuration);
        return new BuildExecutorSelection(BuildExecutorBackend.Kubernetes, buildNamespace);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}
