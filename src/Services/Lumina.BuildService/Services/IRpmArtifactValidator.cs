namespace Lumina.BuildService.Services;

/// <summary>
/// Validates that a build output is an RPM package with readable identity
/// metadata before it is registered as an artifact.
/// </summary>
public interface IRpmArtifactValidator
{
    Task<RpmValidationResult> ValidateAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record RpmValidationResult(
    bool IsValid,
    string? Nevra,
    string? Architecture,
    string? ExpectedFileName,
    string? Error)
{
    public static RpmValidationResult Valid(
        string nevra,
        string architecture,
        string expectedFileName) =>
        new(true, nevra, architecture, expectedFileName, null);

    public static RpmValidationResult Invalid(string error) => new(false, null, null, null, error);
}
