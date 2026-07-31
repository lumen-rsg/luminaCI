using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

internal sealed record NativePromotionGateResultCandidate(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("nevra")] string Nevra);

internal sealed record NativePromotionGateResult(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("promotionSetId")] Guid PromotionSetId,
    [property: JsonPropertyName("kubernetesJobUid")] string KubernetesJobUid,
    [property: JsonPropertyName("runnerImageDigest")] string RunnerImageDigest,
    [property: JsonPropertyName("targetArchitecture")] string TargetArchitecture,
    [property: JsonPropertyName("transaction")] string Transaction,
    [property: JsonPropertyName("candidates")] IReadOnlyList<NativePromotionGateResultCandidate> Candidates);

internal static class NativePromotionGateResultPolicy
{
    private const string Marker = "LUMINA_GATE_RESULT=";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static string ValidateAndHash(
        NativePromotionGate gate,
        KubernetesBuildResourceIdentity identity,
        string logs)
    {
        var lines = (logs ?? string.Empty).Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(Marker, StringComparison.Ordinal))
            .ToList();
        if (lines.Count != 1)
            throw new ValidationException("Native gate did not emit exactly one result document.");
        var payload = lines[0][Marker.Length..];
        NativePromotionGateResult result;
        try
        {
            result = JsonSerializer.Deserialize<NativePromotionGateResult>(payload, Json)
                     ?? throw new JsonException("Result is missing.");
        }
        catch (JsonException exception)
        {
            throw new ValidationException("Native gate result document is invalid.", exception);
        }

        var manifest = NativePromotionGateManifestPolicy.Read(gate);
        var expected = manifest.Candidates
            .OrderBy(item => item.FileName, StringComparer.Ordinal)
            .Select(item => (item.FileName, item.Sha256)).ToList();
        var resultCandidates = result.Candidates ?? [];
        var actual = resultCandidates
            .OrderBy(item => item.FileName, StringComparer.Ordinal)
            .Select(item => (item.FileName, item.Sha256)).ToList();
        if (result.Version != 1 || result.PromotionSetId != gate.Id ||
            result.KubernetesJobUid != identity.JobUid ||
            result.RunnerImageDigest != gate.RunnerImageDigest ||
            result.TargetArchitecture != gate.TargetArchitecture ||
            result.Transaction != "clean-install" || !expected.SequenceEqual(actual) ||
            resultCandidates.Any(item => string.IsNullOrWhiteSpace(item.Nevra) || item.Nevra.Length > 512))
            throw new ValidationException("Native gate result provenance does not match its candidate manifest.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
