using System.Text.RegularExpressions;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

public sealed record KubernetesRunner(
    string BuildProfile,
    string Architecture,
    string Image);

/// <summary>
/// Resolves a target to one operator-managed, digest-pinned Fedora runner.
/// Repository metadata never supplies Kubernetes images or scheduling rules.
/// </summary>
public static partial class KubernetesBuildPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Architectures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fedora-44-x86_64"] = "amd64",
            ["fedora-44-aarch64"] = "arm64"
        };

    public static KubernetesRunner ResolveRunner(
        IConfiguration configuration,
        BuildJob job,
        string? requestedImage)
    {
        if (!Architectures.TryGetValue(job.BuildProfile, out var architecture))
            throw new ValidationException($"Build profile '{job.BuildProfile}' has no Kubernetes runner policy.");
        if (!string.Equals(job.TargetDistribution, "fedora", StringComparison.Ordinal) ||
            !string.Equals(job.TargetRelease, "44", StringComparison.Ordinal) ||
            !string.Equals(job.TargetArchitecture, architecture == "arm64" ? "aarch64" : "x86_64", StringComparison.Ordinal))
        {
            throw new ValidationException("Build target does not match its Kubernetes runner profile.");
        }

        var image = configuration[$"Kubernetes:RunnerImages:{job.BuildProfile}"]?.Trim();
        if (string.IsNullOrWhiteSpace(image))
        {
            throw new InvalidOperationException(
                $"Kubernetes runner image for '{job.BuildProfile}' is not configured.");
        }
        if (!DigestImagePattern().IsMatch(image))
        {
            throw new InvalidOperationException(
                $"Kubernetes runner image for '{job.BuildProfile}' must be pinned by a full SHA-256 digest.");
        }
        if (!string.IsNullOrWhiteSpace(requestedImage) &&
            !string.Equals(requestedImage.Trim(), image, StringComparison.Ordinal))
        {
            throw new ValidationException(
                "Pipeline runner image does not match the administrator-managed Kubernetes runner.");
        }

        return new KubernetesRunner(job.BuildProfile, architecture, image);
    }

    public static void ValidateRunner(KubernetesRunner runner, BuildJob job)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(job);
        if (!Architectures.TryGetValue(runner.BuildProfile, out var expectedArchitecture) ||
            !string.Equals(runner.Architecture, expectedArchitecture, StringComparison.Ordinal) ||
            !string.Equals(job.BuildProfile, runner.BuildProfile, StringComparison.Ordinal) ||
            !DigestImagePattern().IsMatch(runner.Image ?? string.Empty))
        {
            throw new ValidationException("Kubernetes runner does not match the approved build target policy.");
        }
    }

    public static KubernetesJobLimits ResolveLimits(IConfiguration configuration)
    {
        var activeDeadline = ReadBoundedInt(
            configuration, "Kubernetes:Jobs:ActiveDeadlineSeconds", 7200, 60, 86400);
        var ttl = ReadBoundedInt(
            configuration, "Kubernetes:Jobs:TtlSecondsAfterFinished", 86400, 300, 604800);
        var cpuRequest = ReadCpuQuantity(configuration, "Kubernetes:Jobs:CpuRequest", "500m");
        var cpuLimit = ReadCpuQuantity(configuration, "Kubernetes:Jobs:CpuLimit", "2");
        var memoryRequest = ReadBinaryQuantity(configuration, "Kubernetes:Jobs:MemoryRequest", "1Gi");
        var memoryLimit = ReadBinaryQuantity(configuration, "Kubernetes:Jobs:MemoryLimit", "4Gi");
        var storageRequest = ReadBinaryQuantity(configuration, "Kubernetes:Jobs:EphemeralStorageRequest", "4Gi");
        var storageLimit = ReadBinaryQuantity(configuration, "Kubernetes:Jobs:EphemeralStorageLimit", "16Gi");
        if (ParseCpu(cpuRequest) > ParseCpu(cpuLimit) ||
            ParseBinary(memoryRequest) > ParseBinary(memoryLimit) ||
            ParseBinary(storageRequest) > ParseBinary(storageLimit))
        {
            throw new InvalidOperationException("Kubernetes Job resource requests cannot exceed their limits.");
        }
        return new KubernetesJobLimits(
            activeDeadline, ttl, cpuRequest, cpuLimit,
            memoryRequest, memoryLimit, storageRequest, storageLimit);
    }

    private static int ReadBoundedInt(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        return value;
    }

    private static string ReadCpuQuantity(
        IConfiguration configuration,
        string key,
        string defaultValue)
    {
        var value = configuration[key]?.Trim();
        value = string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        if (!CpuQuantityPattern().IsMatch(value))
            throw new InvalidOperationException($"{key} is not a supported CPU quantity.");
        return value;
    }

    private static string ReadBinaryQuantity(
        IConfiguration configuration,
        string key,
        string defaultValue)
    {
        var value = configuration[key]?.Trim();
        value = string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        if (!BinaryQuantityPattern().IsMatch(value))
            throw new InvalidOperationException($"{key} is not a supported memory or storage quantity.");
        return value;
    }

    private static long ParseCpu(string value) =>
        value.EndsWith('m')
            ? long.Parse(value[..^1])
            : checked(long.Parse(value) * 1000);

    private static long ParseBinary(string value)
    {
        var suffixLength = char.IsLetter(value[^1]) ? 2 : 0;
        var amount = long.Parse(suffixLength == 0 ? value : value[..^suffixLength]);
        var multiplier = suffixLength == 0 ? 1L : value[^2..] switch
        {
            "Ki" => 1L << 10,
            "Mi" => 1L << 20,
            "Gi" => 1L << 30,
            "Ti" => 1L << 40,
            _ => throw new InvalidOperationException("Unsupported binary resource suffix.")
        };
        return checked(amount * multiplier);
    }

    [GeneratedRegex("^(?:[a-z0-9.-]+(?::[0-9]+)?/)?[a-z0-9._-]+(?:/[a-z0-9._-]+)*@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestImagePattern();

    [GeneratedRegex("^(?:[1-9][0-9]{0,5}|[1-9][0-9]{0,8}m)$", RegexOptions.CultureInvariant)]
    private static partial Regex CpuQuantityPattern();

    [GeneratedRegex("^[1-9][0-9]{0,5}(?:Ki|Mi|Gi|Ti)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BinaryQuantityPattern();
}

public sealed record KubernetesJobLimits(
    int ActiveDeadlineSeconds,
    int TtlSecondsAfterFinished,
    string CpuRequest,
    string CpuLimit,
    string MemoryRequest,
    string MemoryLimit,
    string EphemeralStorageRequest,
    string EphemeralStorageLimit);
