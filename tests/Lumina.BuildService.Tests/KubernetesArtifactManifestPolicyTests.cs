using Lumina.BuildService.Services;
using Lumina.Shared.Errors;
using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class KubernetesArtifactManifestPolicyTests
{
    [Fact]
    public void Validate_NormalizesSortsAndHashesCanonicalManifest()
    {
        var job = Job();
        var first = Entry(job, "z-tools-1.0-1.aarch64.rpm", 'b');
        var second = Entry(job, "kernel-1.0-1.aarch64.rpm", 'a') with
        {
            Sha256 = new string('A', 64)
        };

        var validated = KubernetesArtifactManifestPolicy.Validate(
            Manifest(job, [first, second]),
            job);
        var reordered = KubernetesArtifactManifestPolicy.Validate(
            Manifest(job, [second, first]),
            job);

        Assert.Equal(64, validated.ManifestSha256.Length);
        Assert.Equal(validated.ManifestSha256, reordered.ManifestSha256);
        Assert.Equal("kernel-1.0-1.aarch64.rpm", validated.Artifacts[0].FileName);
        Assert.Equal(new string('a', 64), validated.Artifacts[0].Sha256);
    }

    [Fact]
    public void Validate_RejectsJobAndRunnerProvenanceMismatch()
    {
        var job = Job();
        var manifest = Manifest(job, [Entry(job, "pkg-1-1.aarch64.rpm", 'a')]);

        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                manifest with { BuildJobId = Guid.NewGuid() }, job));
        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                manifest with { KubernetesJobUid = "another-uid" }, job));
        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                manifest with { RunnerImageDigest = "sha256:" + new string('b', 64) }, job));
        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                manifest with { TargetArchitecture = "x86_64" }, job));
    }

    [Theory]
    [InlineData("../pkg.rpm")]
    [InlineData("nested/pkg.rpm")]
    [InlineData("pkg RPM.rpm")]
    [InlineData("pkg.txt")]
    public void Validate_RejectsUnsafeArtifactFilename(string fileName)
    {
        var job = Job();
        var entry = new KubernetesArtifactManifestEntry(
            fileName,
            "jobs/attacker/object",
            100,
            new string('a', 64));

        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(Manifest(job, [entry]), job));
    }

    [Fact]
    public void Validate_RejectsObjectOutsideExactContentAddressedJobPrefix()
    {
        var job = Job();
        var entry = Entry(job, "pkg-1-1.aarch64.rpm", 'a') with
        {
            ObjectName = "jobs/another-build/sha256/object.rpm"
        };

        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(Manifest(job, [entry]), job));
    }

    [Fact]
    public void Validate_RejectsDuplicateDigestAndUnboundedSize()
    {
        var job = Job();
        var first = Entry(job, "pkg-1-1.aarch64.rpm", 'a');
        var duplicateDigest = Entry(job, "pkg-devel-1-1.aarch64.rpm", 'a');

        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                Manifest(job, [first, duplicateDigest]), job));
        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(
                Manifest(job, [first with { Size = KubernetesArtifactManifestPolicy.MaximumArtifactBytes + 1 }]),
                job));
        Assert.Throws<ValidationException>(() =>
            KubernetesArtifactManifestPolicy.Validate(Manifest(job, [null!]), job));
    }

    private static KubernetesArtifactManifest Manifest(
        BuildJob job,
        IReadOnlyList<KubernetesArtifactManifestEntry> entries) => new(
        KubernetesArtifactManifestPolicy.CurrentVersion,
        job.Id,
        job.KubernetesJobUid!,
        job.RunnerImageDigest!,
        job.TargetDistribution,
        job.TargetRelease,
        job.TargetArchitecture,
        entries);

    private static KubernetesArtifactManifestEntry Entry(
        BuildJob job,
        string fileName,
        char digestCharacter)
    {
        var digest = new string(digestCharacter, 64);
        return new KubernetesArtifactManifestEntry(
            fileName,
            KubernetesArtifactManifestPolicy.ObjectName(
                job.Id,
                job.KubernetesJobUid!,
                digest,
                fileName),
            1024,
            digest);
    }

    private static BuildJob Job() => new()
    {
        Id = Guid.NewGuid(),
        ExecutionBackend = BuildExecutorBackend.Kubernetes,
        KubernetesJobUid = "12345678-1234-1234-1234-123456789abc",
        RunnerImageDigest = "sha256:" + new string('a', 64),
        TargetDistribution = "fedora",
        TargetRelease = "44",
        TargetArchitecture = "aarch64"
    };
}
