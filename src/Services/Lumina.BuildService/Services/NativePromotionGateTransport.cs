using System.Text.Json;
using System.Text.Json.Serialization;
using k8s.Models;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;

namespace Lumina.BuildService.Services;

public sealed record NativePromotionGateTransportCandidate(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("candidatePackageId")] Guid CandidatePackageId,
    [property: JsonPropertyName("projectPackageId")] string ProjectPackageId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record NativePromotionGateTransport(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("promotionSetId")] Guid PromotionSetId,
    [property: JsonPropertyName("repositoryId")] Guid RepositoryId,
    [property: JsonPropertyName("kubernetesJobUid")] string KubernetesJobUid,
    [property: JsonPropertyName("candidateManifestSha256")] string CandidateManifestSha256,
    [property: JsonPropertyName("targetDistribution")] string TargetDistribution,
    [property: JsonPropertyName("targetRelease")] string TargetRelease,
    [property: JsonPropertyName("targetArchitecture")] string TargetArchitecture,
    [property: JsonPropertyName("runnerImageDigest")] string RunnerImageDigest,
    [property: JsonPropertyName("bundleObjectName")] string BundleObjectName,
    [property: JsonPropertyName("bundleSha256")] string BundleSha256,
    [property: JsonPropertyName("bundleSize")] long BundleSize,
    [property: JsonPropertyName("bundleDownloadUrl")] string BundleDownloadUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("candidates")] IReadOnlyList<NativePromotionGateTransportCandidate> Candidates);

internal static class NativePromotionGateTransportPolicy
{
    public const int CurrentVersion = 1;
    public const string TransportDataKey = "gate.json";
    public const string ScriptDataKey = "run.sh";
    public const string VolumeName = "gate-transport";
    public const string MountPath = "/run/lumina-gate";
    private const int ExpiryBufferSeconds = 15 * 60;
    private const int MaximumExpirySeconds = 7 * 24 * 60 * 60;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<NativePromotionGateTransport> CreateAsync(
        NativePromotionGate gate,
        KubernetesBuildResourceIdentity identity,
        KubernetesJobLimits limits,
        IKubernetesObjectUrlSigner signer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var manifest = NativePromotionGateManifestPolicy.Read(gate);
        if (!string.Equals(identity.JobName, NativePromotionGateIdentity.JobName(gate.Id), StringComparison.Ordinal) ||
            !string.Equals(identity.Namespace, gate.KubernetesNamespace, StringComparison.Ordinal) ||
            !string.Equals(identity.JobUid, gate.KubernetesJobUid, StringComparison.Ordinal))
            throw new ValidationException("Native promotion gate transport identity is invalid.");

        var expirySeconds = checked((int)Math.Min(
            MaximumExpirySeconds,
            (long)limits.ActiveDeadlineSeconds + ExpiryBufferSeconds));
        if (string.IsNullOrWhiteSpace(gate.BundleObjectName) ||
            gate.BundleSha256 is not { Length: 64 } || gate.BundleSize is null or <= 0 ||
            gate.BundlePreparedAt is null)
            throw new ValidationException("Native promotion gate has no immutable repository bundle.");
        var bundleUrl = await signer.SignArtifactDownloadAsync(
            gate.BundleObjectName, expirySeconds, cancellationToken);
        ValidateUrl(bundleUrl);
        var candidates = manifest.Candidates.Select(candidate =>
            new NativePromotionGateTransportCandidate(
                candidate.ArtifactId, candidate.CandidatePackageId, candidate.ProjectPackageId,
                candidate.FileName, candidate.Size, candidate.Sha256)).ToList();
        return new NativePromotionGateTransport(
            CurrentVersion, gate.Id, gate.RepositoryId, identity.JobUid,
            gate.CandidateManifestSha256, "fedora", "44", gate.TargetArchitecture,
            gate.RunnerImageDigest, gate.BundleObjectName, gate.BundleSha256,
            gate.BundleSize.Value, bundleUrl, now.AddSeconds(expirySeconds), candidates);
    }

    public static V1Secret CreateSecret(
        string buildNamespace,
        string jobName,
        NativePromotionGateTransport transport)
    {
        Validate(jobName, transport);
        if (string.IsNullOrWhiteSpace(buildNamespace))
            throw new ValidationException("Native promotion gate namespace is required.");
        return new V1Secret
        {
            ApiVersion = "v1",
            Kind = "Secret",
            Immutable = true,
            Type = "Opaque",
            Metadata = new V1ObjectMeta
            {
                Name = SecretName(jobName),
                NamespaceProperty = buildNamespace,
                Labels = new Dictionary<string, string>
                {
                    [NativePromotionGateJobFactory.GateIdLabel] = transport.PromotionSetId.ToString("N"),
                    ["app.kubernetes.io/managed-by"] = "lumina-ci"
                },
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "batch/v1",
                        Kind = "Job",
                        Name = jobName,
                        Uid = transport.KubernetesJobUid,
                        Controller = true
                    }
                ]
            },
            Data = new Dictionary<string, byte[]>
            {
                [TransportDataKey] = JsonSerializer.SerializeToUtf8Bytes(transport, Json),
                [ScriptDataKey] = System.Text.Encoding.UTF8.GetBytes(RunnerScript)
            }
        };
    }

    public static NativePromotionGateTransport ValidateSecret(
        V1Secret secret,
        KubernetesBuildResourceIdentity identity,
        NativePromotionGate gate)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (!string.Equals(secret.Metadata?.Name, SecretName(identity.JobName), StringComparison.Ordinal) ||
            !string.Equals(secret.Metadata?.NamespaceProperty, identity.Namespace, StringComparison.Ordinal) ||
            secret.Immutable != true || secret.Data is not { Count: 2 } ||
            !secret.Data.TryGetValue(TransportDataKey, out var transportBytes) ||
            !secret.Data.TryGetValue(ScriptDataKey, out var scriptBytes) ||
            !scriptBytes.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(RunnerScript)) ||
            secret.Metadata?.OwnerReferences is not [{ Kind: "Job" } owner] ||
            !string.Equals(owner.Name, identity.JobName, StringComparison.Ordinal) ||
            !string.Equals(owner.Uid, identity.JobUid, StringComparison.Ordinal))
            throw new ConflictException("Existing native gate Secret identity is invalid.");
        NativePromotionGateTransport persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<NativePromotionGateTransport>(transportBytes, Json)
                        ?? throw new JsonException("Transport is missing.");
        }
        catch (JsonException exception)
        {
            throw new ConflictException("Existing native gate Secret is invalid.", exception);
        }
        Validate(identity.JobName, persisted);
        if (persisted.PromotionSetId != gate.Id || persisted.RepositoryId != gate.RepositoryId ||
            persisted.KubernetesJobUid != identity.JobUid ||
            !string.Equals(persisted.CandidateManifestSha256, gate.CandidateManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(persisted.TargetArchitecture, gate.TargetArchitecture, StringComparison.Ordinal) ||
            !string.Equals(persisted.RunnerImageDigest, gate.RunnerImageDigest, StringComparison.Ordinal) ||
            persisted.BundleObjectName != gate.BundleObjectName ||
            persisted.BundleSha256 != gate.BundleSha256 || persisted.BundleSize != gate.BundleSize)
            throw new ConflictException("Existing native gate Secret provenance changed.");
        return persisted;
    }

    public static string SecretName(string jobName) => $"{jobName}-input";

    private static void Validate(string jobName, NativePromotionGateTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport.Version != CurrentVersion || transport.PromotionSetId == Guid.Empty ||
            transport.RepositoryId == Guid.Empty ||
            !string.Equals(jobName, NativePromotionGateIdentity.JobName(transport.PromotionSetId), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(transport.KubernetesJobUid) || transport.KubernetesJobUid.Length > 128 ||
            transport.CandidateManifestSha256.Length != 64 ||
            transport.CandidateManifestSha256.Any(character => !Uri.IsHexDigit(character)) ||
            transport.BundleObjectName != $"promotion-gates/{transport.PromotionSetId:N}/sha256/{transport.BundleSha256}/input.tar" ||
            transport.BundleSha256.Length != 64 || transport.BundleSha256.Any(character => !Uri.IsHexDigit(character)) ||
            transport.BundleSize is <= 0 or > 8L * 1024 * 1024 * 1024 ||
            transport.ExpiresAt <= DateTimeOffset.UtcNow ||
            transport.Candidates is not { Count: > 0 and <= 4096 })
            throw new ValidationException("Native promotion gate transport is invalid.");
        ValidateUrl(transport.BundleDownloadUrl);
        foreach (var candidate in transport.Candidates)
        {
            if (candidate.ArtifactId == Guid.Empty || candidate.CandidatePackageId == Guid.Empty ||
                candidate.Size <= 0 || candidate.Sha256.Length != 64 ||
                candidate.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                Path.GetFileName(candidate.FileName) != candidate.FileName ||
                string.IsNullOrWhiteSpace(candidate.ProjectPackageId))
                throw new ValidationException("Native promotion gate transport candidate is invalid.");
        }
    }

    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrWhiteSpace(uri.Query) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ValidationException("Native promotion gate download URL is invalid.");
    }

    public const string RunnerScript = """
#!/bin/bash
set -euo pipefail
umask 077

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
readonly transport=/run/lumina-gate/gate.json
readonly work=/workspace/native-gate
readonly bundle_tar=${work}/input.tar
readonly bundle=${work}/bundle
[[ -r "${transport}" ]] || fail "native gate transport is unavailable"

jq -e 'type == "object" and .version == 1 and
  (.promotionSetId | type == "string" and test("^[0-9a-f-]{36}$")) and
  (.repositoryId | type == "string" and test("^[0-9a-f-]{36}$")) and
  (.kubernetesJobUid | type == "string" and length >= 1 and length <= 128) and
  (.candidateManifestSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
  .targetDistribution == "fedora" and .targetRelease == "44" and
  (.targetArchitecture == "x86_64" or .targetArchitecture == "aarch64") and
  (.runnerImageDigest | type == "string" and test("^sha256:[0-9a-f]{64}$")) and
  (.bundleObjectName | type == "string" and length >= 1 and length <= 1024) and
  (.bundleSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
  (.bundleSize | type == "number" and . > 0 and floor == .) and
  (.bundleDownloadUrl | type == "string" and test("^https://[^[:space:]]+\\?[^[:space:]]+$")) and
  (.candidates | type == "array" and length >= 1 and length <= 4096)' "${transport}" >/dev/null \
  || fail "native gate transport is invalid"

promotion_set_id=$(jq -r '.promotionSetId' "${transport}")
job_uid=$(jq -r '.kubernetesJobUid' "${transport}")
target_arch=$(jq -r '.targetArchitecture' "${transport}")
runner_digest=$(jq -r '.runnerImageDigest' "${transport}")
bundle_size=$(jq -r '.bundleSize' "${transport}")
bundle_hash=$(jq -r '.bundleSha256' "${transport}")
bundle_url=$(jq -r '.bundleDownloadUrl' "${transport}")
[[ "${LUMINA_PROMOTION_SET_ID:-}" == "${promotion_set_id}" ]] || fail "promotion set identity mismatch"
[[ "${RUNNER_IMAGE_DIGEST:-}" == "${runner_digest}" ]] || fail "runner image digest mismatch"
[[ "$(. /etc/os-release && printf '%s' "${ID}")" == fedora ]] || fail "native gate runner is not Fedora"
[[ "$(. /etc/os-release && printf '%s' "${VERSION_ID}")" == 44 ]] || fail "native gate runner release mismatch"
[[ "$(rpm --eval '%{_target_cpu}')" == "${target_arch}" ]] || fail "native gate runner architecture mismatch"

mkdir -p "${bundle}"
curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
  --connect-timeout 30 --max-time 1800 --max-filesize "${bundle_size}" \
  --output "${bundle_tar}" "${bundle_url}"
[[ "$(stat -c '%s' "${bundle_tar}")" == "${bundle_size}" ]] || fail "gate bundle size mismatch"
[[ "$(sha256sum "${bundle_tar}" | awk '{print $1}')" == "${bundle_hash}" ]] || fail "gate bundle digest mismatch"
while IFS= read -r entry; do
  [[ -n "${entry}" && "${entry}" != /* && "${entry}" != *'../'* && "${entry}" != '..' ]] \
    || fail "gate bundle contains an unsafe path"
done < <(tar --list --file "${bundle_tar}")
while IFS= read -r type; do
  [[ "${type}" == '-' || "${type}" == 'd' ]] || fail "gate bundle contains a link or special entry"
done < <(tar --list --verbose --numeric-owner --file "${bundle_tar}" | cut -c1)
tar --extract --file "${bundle_tar}" --directory "${bundle}" \
  --no-same-owner --no-same-permissions --delay-directory-restore

manifest=${bundle}/manifest.json
jq -e --arg set "${promotion_set_id}" --arg repo "$(jq -r '.repositoryId' "${transport}")" \
  --arg manifestHash "$(jq -r '.candidateManifestSha256' "${transport}")" --arg arch "${target_arch}" \
  '.version == 1 and .promotionSetId == $set and .repositoryId == $repo and
   .candidateManifestSha256 == $manifestHash and .targetArchitecture == $arch and
   (.baselineManifestSha256 | type == "string" and test("^[0-9a-f]{64}$")) and
   (.baselinePackageNames | type == "array") and (.candidates | type == "array")' \
  "${manifest}" >/dev/null || fail "gate bundle manifest identity mismatch"
outer_candidates=$(jq -cS '[.candidates[] | {artifactId,candidatePackageId,projectPackageId,fileName,size,sha256}] | sort_by(.artifactId)' "${transport}")
bundle_candidates=$(jq -cS '[.candidates[] | {artifactId,candidatePackageId,projectPackageId,fileName,size,sha256}] | sort_by(.artifactId)' "${manifest}")
[[ "${outer_candidates}" == "${bundle_candidates}" ]] || fail "gate bundle candidate membership mismatch"

rpms=${bundle}/candidates
entries=${work}/entries.jsonl
: > "${entries}"
while IFS= read -r encoded; do
  item=$(printf '%s' "${encoded}" | base64 --decode)
  file=$(jq -r '.fileName' <<<"${item}")
  size=$(jq -r '.size' <<<"${item}")
  hash=$(jq -r '.sha256' <<<"${item}")
  [[ "${file}" =~ ^[A-Za-z0-9][A-Za-z0-9._+~-]{0,510}\.rpm$ ]] || fail "candidate filename is invalid"
  [[ "${size}" =~ ^[1-9][0-9]*$ && "${hash}" =~ ^[0-9a-f]{64}$ ]] || fail "candidate integrity metadata is invalid"
  path=${rpms}/${file}
  [[ -f "${path}" ]] || fail "candidate RPM is missing from the gate bundle"
  [[ "$(stat -c '%s' "${path}")" == "${size}" ]] || fail "candidate size mismatch"
  [[ "$(sha256sum "${path}" | awk '{print $1}')" == "${hash}" ]] || fail "candidate digest mismatch"
  rpm_arch=$(rpm -qp --qf '%{ARCH}' "${path}")
  [[ "${rpm_arch}" == noarch || "${rpm_arch}" == "${target_arch}" ]] || fail "candidate RPM architecture mismatch"
  nevra=$(rpm -qp --qf '%{NAME}-%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}' "${path}")
  jq -cn --arg fileName "${file}" --arg sha256 "${hash}" --arg nevra "${nevra}" \
    '{fileName:$fileName,sha256:$sha256,nevra:$nevra}' >> "${entries}"
done < <(jq -r '.candidates[] | @base64' "${transport}")

mapfile -d '' candidate_paths < <(find "${rpms}" -maxdepth 1 -type f -name '*.rpm' -print0 | sort -z)
[[ "${#candidate_paths[@]}" == "$(jq '.candidates | length' "${transport}")" ]] \
  || fail "candidate file set is incomplete"

mapfile -t baseline_names < <(jq -r '.baselinePackageNames[]' "${manifest}")
dnf_common=(--disablerepo='*' --enablerepo=lumina-fedora \
  --setopt="lumina-fedora.baseurl=${FEDORA_REPOSITORY_BASE_URL}/releases/44/Everything/${target_arch}/os/" \
  --setopt=install_weak_deps=False --setopt=keepcache=False)
if ((${#baseline_names[@]} > 0)); then
  dnf "${dnf_common[@]}" --repofrompath="lumina-baseline,file://${bundle}/baseline" \
    --enablerepo=lumina-baseline --setopt=lumina-baseline.gpgcheck=0 \
    install -y "${baseline_names[@]}"
fi
dnf "${dnf_common[@]}" --repofrompath="lumina-baseline,file://${bundle}/baseline" \
  --repofrompath="lumina-candidate,file://${bundle}/candidates" \
  --enablerepo=lumina-baseline --enablerepo=lumina-candidate \
  --setopt=lumina-baseline.gpgcheck=0 --setopt=lumina-candidate.gpgcheck=0 \
  install -y "${candidate_paths[@]}"

while IFS= read -r encoded; do
  item=$(printf '%s' "${encoded}" | base64 --decode)
  file=$(jq -r '.fileName' <<<"${item}")
  expected=$(rpm -qp --qf '%{NAME}-%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}' "${rpms}/${file}")
  name=$(rpm -qp --qf '%{NAME}' "${rpms}/${file}")
  installed=$(rpm -q --qf '%{NAME}-%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}' "${name}")
  [[ "${installed}" == "${expected}" ]] || fail "candidate RPM was not selected by the upgrade transaction"
done < <(jq -r '.candidates[] | @base64' "${transport}")

jq -s --argjson version 1 --arg promotionSetId "${promotion_set_id}" \
  --arg kubernetesJobUid "${job_uid}" --arg runnerImageDigest "${runner_digest}" \
  --arg targetArchitecture "${target_arch}" --argjson baselinePackageNames "$(jq -c '.baselinePackageNames' "${manifest}")" \
  '{version:$version,promotionSetId:$promotionSetId,kubernetesJobUid:$kubernetesJobUid,runnerImageDigest:$runnerImageDigest,targetArchitecture:$targetArchitecture,transaction:"baseline-upgrade",baselinePackageNames:$baselinePackageNames,candidates:.}' \
  "${entries}" | jq -cS | sed 's/^/LUMINA_GATE_RESULT=/'
""";
}
