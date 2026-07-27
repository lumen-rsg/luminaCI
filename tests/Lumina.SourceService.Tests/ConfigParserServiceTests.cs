using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.SourceService.Tests;

public sealed class ConfigParserServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private ConfigParserService NewService(string content, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"lumina-conf-{Guid.NewGuid():N}.ini");
        _tempFiles.Add(path);
        File.WriteAllText(path, content);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = path
            })
            .Build();

        return new ConfigParserService(
            NullLogger<ConfigParserService>.Instance,
            configuration);
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    [Fact]
    public void ParsePackages_ParsesStrictValidManifest()
    {
        var service = NewService("""
            [package]
            name="my-package"
            source="https://example.com/repo.git"
            source_type="git"
            source_branch="main"
            build_image="fedora:44"
            spec_path="packaging/my-package.spec"
            """, out _);

        var package = Assert.Single(service.ParsePackages());

        Assert.Equal("my-package", package.Name);
        Assert.Equal("https://example.com/repo.git", package.Source);
        Assert.Equal(SourceType.Git, package.SourceType);
        Assert.Equal("main", package.SourceBranch);
        Assert.Null(package.ExpectedSha256);
        Assert.Equal("fedora:44", package.BuildImage);
        Assert.Equal("packaging/my-package.spec", package.SpecPath);
    }

    [Fact]
    public void ParsePackages_AcceptsCheckedInManifest()
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = Path.Combine(repositoryRoot, "conf.ini")
            })
            .Build();
        var service = new ConfigParserService(
            NullLogger<ConfigParserService>.Instance,
            configuration);

        var packages = service.ParsePackages();
        Assert.Equal(5, packages.Count);
        Assert.Contains(packages, package => package.SpecPath == "Test/test_package.spec");
    }

    [Fact]
    public void ParsePackages_ParsesExpectedArchiveSha256()
    {
        var service = NewService($"""
            [package]
            name=archive
            source=https://example.com/archive.tar.gz
            source_type=tar
            source_sha256={new string('a', 64)}
            """, out _);

        Assert.Equal(new string('a', 64), Assert.Single(service.ParsePackages()).ExpectedSha256);
    }

    [Fact]
    public void ParsePackages_ReturnsIndependentReadOnlySnapshots()
    {
        var service = NewService("""
            [package]
            name=pkg
            source=https://example.com/pkg.tar.gz
            source_type=tar
            """, out _);

        var first = service.ParsePackages();
        var second = service.ParsePackages();

        Assert.NotSame(first, second);
        var mutableView = Assert.IsAssignableFrom<IList<PackageSourceConfig>>(first);
        Assert.Throws<NotSupportedException>(() => mutableView.Add(first[0]));
    }

    [Fact]
    public async Task ParsePackages_IsSafeForConcurrentSingletonReads()
    {
        var service = NewService("""
            [package]
            name=pkg
            source=https://example.com/repo.git
            source_type=git
            """, out _);

        var snapshots = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(service.ParsePackages)));

        Assert.All(snapshots, snapshot => Assert.Equal("pkg", Assert.Single(snapshot).Name));
        Assert.Equal(snapshots.Length, snapshots.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void ParsePackages_ThrowsWhenFileIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ini");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = path
            })
            .Build();
        var service = new ConfigParserService(
            NullLogger<ConfigParserService>.Instance,
            configuration);

        Assert.Throws<ConfigParseException>(service.ParsePackages);
    }

    [Fact]
    public void ParsePackages_DoesNotReturnStaleDataAfterParseFailure()
    {
        var service = NewService("""
            [package]
            name=valid
            source=https://example.com/repo.git
            source_type=git
            """, out var path);
        Assert.Single(service.ParsePackages());

        File.WriteAllText(path, """
            [package]
            name=broken
            source_type=git
            """);

        Assert.Throws<ConfigParseException>(service.ParsePackages);
    }

    [Theory]
    [InlineData("""
        [package]
        source=https://example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        """)]
    [InlineData("""
        [package]
        name=pkg
        sources=https://example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        source_type=git
        unexpected=value
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        source_type=unknown
        """)]
    [InlineData("""
        [package]
        name=pkg
        name=other
        source=https://example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=ftp://example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=rsync://example.com/pkg
        source_type=rsync
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=ssh://example.com/pkg
        source_type=hg
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://user:secret@example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        source_type=git
        spec_path=../pkg.spec
        """)]
    [InlineData("""
        [package]
        name=../pkg
        source=https://example.com/repo.git
        source_type=git
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        source_type=git
        source_sha256=not-a-sha
        """)]
    [InlineData("""
        [package]
        name=pkg
        source=https://example.com/repo.git
        source_type=git
        source_branch=
        """)]
    [InlineData("""
        [other]
        name=pkg
        source=https://example.com/repo.git
        source_type=git
        """)]
    [InlineData("name=pkg")]
    [InlineData("# no packages")]
    public void ParsePackages_RejectsInvalidManifest(string content)
    {
        var service = NewService(content, out _);

        Assert.Throws<ConfigParseException>(service.ParsePackages);
    }

    [Fact]
    public void ParsePackages_RejectsDuplicateNamesCaseInsensitively()
    {
        var service = NewService("""
            [package]
            name=Package
            source=https://example.com/a.git
            source_type=git

            [package]
            name=package
            source=https://example.com/b.git
            source_type=git
            """, out _);

        Assert.Throws<ConfigParseException>(service.ParsePackages);
    }

    [Theory]
    [InlineData("git", "https://example.com/repo.git", SourceType.Git)]
    [InlineData("tar.gz", "https://example.com/pkg.tar.gz", SourceType.Tar)]
    [InlineData("https", "https://example.com/pkg.rpm", SourceType.Http)]
    [InlineData("local", "fixtures/pkg", SourceType.Local)]
    public void ParsePackages_MapsSupportedSourceTypes(
        string sourceType,
        string source,
        SourceType expected)
    {
        var service = NewService($$"""
            [package]
            name=pkg
            source={{source}}
            source_type={{sourceType}}
            """, out _);

        Assert.Equal(expected, Assert.Single(service.ParsePackages()).SourceType);
    }

    [Fact]
    public void GetPackage_IsCaseInsensitive()
    {
        var service = NewService("""
            [package]
            name=MyPackage
            source=https://example.com/repo.git
            source_type=git
            """, out _);

        Assert.Equal("MyPackage", service.GetPackage("mypackage")?.Name);
        Assert.Null(service.GetPackage("missing"));
    }
}
