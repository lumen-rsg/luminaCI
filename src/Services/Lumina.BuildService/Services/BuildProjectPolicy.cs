using System.Text.RegularExpressions;
using Lumina.Shared.DTOs;
using Lumina.Shared.Errors;

namespace Lumina.BuildService.Services;

public sealed record NormalizedBuildProject(
    string Name,
    string GitRepoUrl,
    string GitBranch,
    string ManifestPath,
    string? GitUsername,
    string? GitToken);

public static partial class BuildProjectPolicy
{
    public static string NormalizePackageId(string? packageId)
    {
        var normalized = packageId?.Trim() ?? string.Empty;
        if (!PackageIdPattern().IsMatch(normalized))
        {
            throw new ValidationException(
                "Package ID must start with a lowercase letter or digit and contain at most 128 lowercase letters, digits, '+', '_', '.', or '-'.");
        }
        return normalized;
    }

    public static NormalizedBuildProject Validate(CreateBuildProjectRequest request) =>
        ValidateCore(
            request.Name,
            request.GitRepoUrl,
            request.GitBranch,
            request.ManifestPath,
            request.WebhookSecret,
            requireWebhookSecret: true,
            request.GitUsername,
            request.GitToken);

    public static NormalizedBuildProject Validate(UpdateBuildProjectRequest request) =>
        ValidateCore(
            request.Name,
            request.GitRepoUrl,
            request.GitBranch,
            request.ManifestPath,
            request.WebhookSecret,
            requireWebhookSecret: false,
            request.ClearGitCredentials ? null : request.GitUsername,
            request.ClearGitCredentials ? null : request.GitToken);

    public static void ValidateWebhookSecret(string? secret, bool required)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (required)
                throw new ValidationException("A webhook secret is required.");
            return;
        }

        if (secret.Length is < 32 or > 512 || secret.Any(char.IsControl))
        {
            throw new ValidationException(
                "Webhook secret must contain between 32 and 512 non-control characters.");
        }
    }

    private static NormalizedBuildProject ValidateCore(
        string? name,
        string? gitRepoUrl,
        string? gitBranch,
        string? manifestPath,
        string? webhookSecret,
        bool requireWebhookSecret,
        string? gitUsername,
        string? gitToken)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > 128 || normalizedName.Any(char.IsControl))
            throw new ValidationException("Project name must contain 1 to 128 non-control characters.");

        if (!Uri.TryCreate(gitRepoUrl?.Trim(), UriKind.Absolute, out var repositoryUri) ||
            repositoryUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(repositoryUri.Host) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(repositoryUri.Query) ||
            !string.IsNullOrEmpty(repositoryUri.Fragment))
        {
            throw new ValidationException(
                "Git repository must be an absolute HTTPS URL without credentials, query, or fragment.");
        }

        var normalizedBranch = gitBranch?.Trim() ?? string.Empty;
        if (!GitReferencePattern().IsMatch(normalizedBranch) ||
            normalizedBranch.StartsWith('.') || normalizedBranch.EndsWith('.') ||
            normalizedBranch.Contains("..", StringComparison.Ordinal) ||
            normalizedBranch.Contains("//", StringComparison.Ordinal) ||
            normalizedBranch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Git branch is not a safe branch name.");
        }

        var normalizedManifestPath = NormalizePath(manifestPath);
        ValidateManifestPath(normalizedManifestPath);
        ValidateWebhookSecret(webhookSecret, requireWebhookSecret);

        var normalizedUsername = NormalizeOptional(gitUsername);
        var normalizedToken = NormalizeOptional(gitToken);
        if ((normalizedUsername is null) != (normalizedToken is null))
        {
            throw new ValidationException(
                "Git username and token must either both be supplied or both be omitted.");
        }
        if (normalizedUsername?.Length > 256 || normalizedUsername?.Any(char.IsControl) == true ||
            normalizedToken?.Length > 4096 || normalizedToken?.Any(char.IsControl) == true)
        {
            throw new ValidationException("Git credentials exceed their limits or contain control characters.");
        }

        return new NormalizedBuildProject(
            normalizedName,
            repositoryUri.ToString(),
            normalizedBranch,
            normalizedManifestPath,
            normalizedUsername,
            normalizedToken);
    }

    private static void ValidateManifestPath(string path)
    {
        if (path.Length is < 1 or > 512 || path.StartsWith('/') ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            path.Any(char.IsControl) ||
            !(path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException(
                "Manifest path must be a safe repository-relative .yaml or .yml path.");
        }
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,254}[A-Za-z0-9]$|^[A-Za-z0-9]$")]
    private static partial Regex GitReferencePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9+_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();
}
