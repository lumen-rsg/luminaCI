namespace Lumina.BuildService.Services;

/// <summary>
/// Validates that a build output is an RPM package with readable identity
/// metadata before it is registered as an artifact.
/// </summary>
public interface IRpmArtifactValidator
{
    Task<RpmValidationResult> ValidateAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record RpmValidationResult(bool IsValid, string? Nevra, string? Error)
{
    public static RpmValidationResult Valid(string nevra) => new(true, nevra, null);
    public static RpmValidationResult Invalid(string error) => new(false, null, error);
}
