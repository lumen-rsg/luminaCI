using Lumina.Shared.DTOs;
using Lumina.Shared.Extensions;
using Lumina.Shared.Models.Enums;

namespace Lumina.SourceService.Services;

/// <summary>Validates package metadata before an immutable revision is stored.</summary>
public static class PackageSourcePolicy
{
    public static bool TryNormalizeSlug(string slug, out string normalized)
    {
        try
        {
            normalized = NormalizeSlug(slug);
            return true;
        }
        catch (SourceValidationException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static string NormalizeSlug(string slug)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 100 ||
            normalized is "." or ".." ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new SourceValidationException(
                "Package slug must contain 1-100 ASCII letters, digits, '.', '_' or '-'.");
        }
        return normalized;
    }

    public static void Validate(SavePackageSourceRequest request)
    {
        NormalizeSlug(request.Slug);

        if (request.SourceType is not (SourceType.Git or SourceType.Tar or SourceType.Http))
            throw new SourceValidationException(
                "Only pinned HTTPS Git, tar, and HTTP package sources are supported.");
        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(sourceUri.Host))
        {
            throw new SourceValidationException(
                "Package sources must use an absolute HTTPS URL.");
        }
        if (!string.IsNullOrEmpty(sourceUri.UserInfo))
            throw new SourceValidationException(
                "Source URI must not contain embedded credentials.");

        if (request.SourceType == SourceType.Git)
        {
            if (string.IsNullOrWhiteSpace(request.SourceReference))
                throw new SourceValidationException(
                    "Git sources require an explicit branch, tag, or commit reference.");
            if (request.ExpectedSha256 is not null)
                throw new SourceValidationException(
                    "Expected SHA-256 is only valid for archive sources.");
            try
            {
                ProcessArgumentSanitizer.ValidateGitReference(request.SourceReference);
            }
            catch (ArgumentException ex)
            {
                throw new SourceValidationException(ex.Message);
            }
        }
        else
        {
            if (request.SourceReference is not null)
                throw new SourceValidationException(
                    "Source reference is only valid for Git sources.");
            if (request.ExpectedSha256 is null ||
                request.ExpectedSha256.Length != 64 ||
                request.ExpectedSha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new SourceValidationException(
                    "Archive sources require an expected SHA-256 with 64 hexadecimal characters.");
            }
        }

        ValidateRelativePath(request.SpecPath);

        if (request.BuildImage is { Length: > 256 })
            throw new SourceValidationException("Build image must be at most 256 characters.");
    }

    private static void ValidateRelativePath(string? path)
    {
        if (path is null)
            return;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 512 ||
            Path.IsPathRooted(path) ||
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..") ||
            path.Any(char.IsControl))
        {
            throw new SourceValidationException(
                "Spec path must be a confined relative path of at most 512 characters.");
        }
    }
}
