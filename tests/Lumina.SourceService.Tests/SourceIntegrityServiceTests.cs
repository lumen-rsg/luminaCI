using System.Formats.Tar;
using System.Text;
using Lumina.SourceService.Services;
using Lumina.Shared.Models.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.SourceService.Tests;

public sealed class SourceIntegrityServiceTests
{
    [Fact]
    public async Task ExtractTar_ExtractsBoundedRegularFile()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteEntry("package/readme.txt", "trusted");

        await CreateService().ExtractTarAsync(
            fixture.ArchivePath, fixture.OutputPath, CancellationToken.None);

        Assert.Equal(
            "trusted",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.OutputPath, "package", "readme.txt")));
    }

    [Fact]
    public async Task ExtractTar_RejectsPathTraversal()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteEntry("../escape.txt", "nope");

        await Assert.ThrowsAsync<SourceValidationException>(() =>
            CreateService().ExtractTarAsync(
                fixture.ArchivePath, fixture.OutputPath, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(fixture.RootPath, "escape.txt")));
    }

    [Fact]
    public async Task ExtractTar_RejectsSymbolicLinks()
    {
        using var fixture = new ArchiveFixture();
        fixture.WriteSymbolicLink("package/link", "/etc/passwd");

        await Assert.ThrowsAsync<SourceValidationException>(() =>
            CreateService().ExtractTarAsync(
                fixture.ArchivePath, fixture.OutputPath, CancellationToken.None));
    }

    [Fact]
    public void RedactUri_RemovesCredentialsQueryAndFragment()
    {
        var redacted = SourceIntegrityService.RedactUri(
            new Uri("https://user:secret@example.com/archive.tar?token=secret#part"));

        Assert.Equal("https://example.com/archive.tar", redacted.TrimEnd('/'));
        Assert.DoesNotContain("secret", redacted);
    }

    [Fact]
    public async Task Validator_ReturnsTheApprovedAddressForConnectionPinning()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(
            "https://93.184.216.34/source.tar", SourceType.Tar, null);

        Assert.Equal("93.184.216.34", Assert.Single(result.Addresses).ToString());
    }

    [Theory]
    [InlineData("https://127.0.0.1/source.tar", SourceType.Tar)]
    [InlineData("rsync://example.com/source", SourceType.Rsync)]
    [InlineData("ssh://example.com/repository", SourceType.Git)]
    public async Task Validator_RejectsUnpinnedOrPrivateTransports(
        string source,
        SourceType sourceType)
    {
        var validator = CreateValidator();

        await Assert.ThrowsAsync<SourceValidationException>(() =>
            validator.ValidateAsync(source, sourceType, null));
    }

    private static SourceIntegrityService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:MaxDownloadBytes"] = "1048576",
                ["Source:MaxExpandedBytes"] = "1048576",
                ["Source:MaxFileCount"] = "100",
                ["Source:MaxCompressionRatio"] = "100"
            })
            .Build();
        var validator = CreateValidator(configuration);
        return new SourceIntegrityService(validator, configuration);
    }

    private static SourceUriValidator CreateValidator(
        IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().Build();
        return new SourceUriValidator(
            configuration, NullLogger<SourceUriValidator>.Instance);
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly FileStream _archive;
        private readonly TarWriter _writer;

        public ArchiveFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(), $"lumina-integrity-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            ArchivePath = Path.Combine(RootPath, "source.tar");
            OutputPath = Path.Combine(RootPath, "output");
            _archive = File.Create(ArchivePath);
            _writer = new TarWriter(_archive, leaveOpen: true);
        }

        public string RootPath { get; }
        public string ArchivePath { get; }
        public string OutputPath { get; }

        public void WriteEntry(string path, string content)
        {
            var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
            _writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = data
            });
            Finish();
        }

        public void WriteSymbolicLink(string path, string target)
        {
            _writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, path)
            {
                LinkName = target
            });
            Finish();
        }

        private void Finish()
        {
            _writer.Dispose();
            _archive.Dispose();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _archive.Dispose();
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
