using Lumina.BuildService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.BuildService.Tests;

public sealed class RpmArtifactValidatorTests
{
    private static RpmArtifactValidator CreateValidator()
        => new(
            new ConfigurationBuilder().Build(),
            NullLogger<RpmArtifactValidator>.Instance);

    private static RpmArtifactValidator CreateValidator(string executable)
        => new(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rpm:Executable"] = executable
            }).Build(),
            NullLogger<RpmArtifactValidator>.Instance);

    [Fact]
    public async Task ValidateAsync_RejectsMissingFile()
    {
        var result = await CreateValidator().ValidateAsync(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rpm"));

        Assert.False(result.IsValid);
        Assert.Equal("file does not exist", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_RejectsRenamedNonRpmWithoutStartingRpmProcess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rpm");
        await File.WriteAllTextAsync(path, "not an rpm");

        try
        {
            var result = await CreateValidator().ValidateAsync(path);

            Assert.False(result.IsValid);
            Assert.Equal("RPM lead magic is invalid", result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsTruncatedRpmLead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.rpm");
        await File.WriteAllBytesAsync(path, [0xed, 0xab, 0xee, 0xdb]);

        try
        {
            var result = await CreateValidator().ValidateAsync(path);

            Assert.False(result.IsValid);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNevraAndHeaderArchitecture()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"lumina-rpm-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var rpmPath = Path.Combine(directory, "package.rpm");
        var executable = Path.Combine(directory, "fake-rpm");
        await File.WriteAllBytesAsync(rpmPath, [0xed, 0xab, 0xee, 0xdb, 1]);
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\nprintf 'kernel-0:1.0-1.aarch64\\taarch64\\tkernel-1.0-1.aarch64.rpm'");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var result = await CreateValidator(executable).ValidateAsync(rpmPath);

            Assert.True(result.IsValid);
            Assert.Equal("kernel-0:1.0-1.aarch64", result.Nevra);
            Assert.Equal("aarch64", result.Architecture);
            Assert.Equal("kernel-1.0-1.aarch64.rpm", result.ExpectedFileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
