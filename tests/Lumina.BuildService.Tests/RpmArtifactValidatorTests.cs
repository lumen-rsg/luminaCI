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
}
