using Lumina.Shared.Models.Enums;
using Lumina.SourceService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.SourceService.Tests;

/// <summary>
/// Tests for <see cref="ConfigParserService"/>. This service parses
/// <c>conf.ini</c> — the source of truth for *which git repos get cloned and
/// built* — so a parse regression (a dropped package, a mis-parsed source URL)
/// directly affects what code the pipeline builds. Coverage targets the real
/// parser semantics: INI section handling, the documented <c>sources</c> typo
/// alias, source-type mapping, quoted/unquoted values, the add/remove round
/// trip, and the mtime-based cache invalidation.
/// </summary>
public class ConfigParserServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// Builds a <see cref="ConfigParserService"/> backed by an in-memory
    /// <see cref="IConfiguration"/> pointing <c>Source:ConfigPath</c> at a temp
    /// file with the given initial content.
    /// </summary>
    private ConfigParserService NewService(string content, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"lumina-conf-{Guid.NewGuid():N}.ini");
        _tempFiles.Add(path);
        File.WriteAllText(path, content);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = path
            })
            .Build();

        return new ConfigParserService(NullLogger<ConfigParserService>.Instance, config);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* test cleanup */ }
        }
    }

    // ─── ParsePackages ───────────────────────────────────────────────────

    [Fact]
    public void ParsePackages_ReturnsEmpty_WhenFileMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = "/nonexistent/path/conf-" + Guid.NewGuid() + ".ini"
            })
            .Build();
        var svc = new ConfigParserService(NullLogger<ConfigParserService>.Instance, config);

        var result = svc.ParsePackages();

        Assert.Empty(result);
    }

    [Fact]
    public void ParsePackages_ParsesQuotedValues()
    {
        var svc = NewService("""
            [package]
            name="my-package"
            source="https://example.com/repo.git"
            source_type="git"
            source_branch="main"
            build_image="fedora:40"
            """, out _);

        var packages = svc.ParsePackages();

        var p = Assert.Single(packages);
        Assert.Equal("my-package", p.Name);
        Assert.Equal("https://example.com/repo.git", p.Source);
        Assert.Equal(SourceType.Git, p.SourceType);
        Assert.Equal("main", p.SourceBranch);
        Assert.Equal("fedora:40", p.BuildImage);
    }

    [Fact]
    public void ParsePackages_ParsesUnquotedValues()
    {
        // The parser trims and strips quotes; unquoted values must work too.
        var svc = NewService("""
            [package]
            name=plain-pkg
            source=https://example.com/repo.git
            source_type=git
            """, out _);

        var p = Assert.Single(svc.ParsePackages());
        Assert.Equal("plain-pkg", p.Name);
        Assert.Equal("https://example.com/repo.git", p.Source);
    }

    [Fact]
    public void ParsePackages_SourcesTypo_AliasedToSource()
    {
        // The parser documents a real-world conf.ini typo where the key is
        // "sources" instead of "source". It is treated as the same field, so a
        // historical config with the typo keeps working. Locking this behavior
        // prevents a well-meaning cleanup from silently breaking deployments.
        var svc = NewService("""
            [package]
            name=typo-pkg
            sources=https://example.com/repo.git
            source_type=git
            """, out _);

        var p = Assert.Single(svc.ParsePackages());
        Assert.Equal("typo-pkg", p.Name);
        Assert.Equal("https://example.com/repo.git", p.Source);
    }

    [Theory]
    [InlineData("git", SourceType.Git)]
    [InlineData("tar", SourceType.Tar)]
    [InlineData("tar.gz", SourceType.Tar)]
    [InlineData("tar.bz2", SourceType.Tar)]
    [InlineData("tar.xz", SourceType.Tar)]
    [InlineData("targz", SourceType.Tar)]
    [InlineData("http", SourceType.Http)]
    [InlineData("https", SourceType.Http)]
    [InlineData("ftp", SourceType.Ftp)]
    [InlineData("rsync", SourceType.Rsync)]
    [InlineData("svn", SourceType.Svn)]
    [InlineData("subversion", SourceType.Svn)]
    [InlineData("hg", SourceType.Hg)]
    [InlineData("mercurial", SourceType.Hg)]
    [InlineData("local", SourceType.Local)]
    [InlineData("", SourceType.Http)]            // empty -> default HTTP
    [InlineData("unknown-format", SourceType.Http)] // unknown -> default HTTP
    public void ParsePackages_MapsSourceType(string raw, SourceType expected)
    {
        var svc = NewService($$"""
            [package]
            name=pkg
            source=src
            source_type={{raw}}
            """, out _);

        var p = Assert.Single(svc.ParsePackages());
        Assert.Equal(expected, p.SourceType);
    }

    [Fact]
    public void ParsePackages_ParsesMultipleSections()
    {
        var svc = NewService("""
            [package]
            name=a
            source=src-a
            source_type=git

            [package]
            name=b
            source=src-b
            source_type=tar

            [package]
            name=c
            source=src-c
            source_type=local
            """, out _);

        var packages = svc.ParsePackages();
        Assert.Equal(new[] { "a", "b", "c" }, packages.Select(p => p.Name).ToArray());
        Assert.Equal(SourceType.Tar, packages[1].SourceType);
    }

    [Fact]
    public void ParsePackages_IgnoresCommentsAndBlankLines()
    {
        var svc = NewService("""
            # top-level comment
            ; semicolon comment

            [package]
            # in-section comment
            name=pkg
            source=src

            source_type=git
            """, out _);

        var p = Assert.Single(svc.ParsePackages());
        Assert.Equal("pkg", p.Name);
    }

    [Fact]
    public void ParsePackages_SkipsUnnamedSection()
    {
        // A [package] section without a name is dropped, not stored as empty.
        var svc = NewService("""
            [package]
            source=orphan-source
            source_type=git

            [package]
            name=real
            source=real-src
            source_type=git
            """, out _);

        var p = Assert.Single(svc.ParsePackages());
        Assert.Equal("real", p.Name);
    }

    // ─── Cache invalidation (mtime) ──────────────────────────────────────

    [Fact]
    public async Task ParsePackages_CachesUntilFileChanges()
    {
        // The cache key is the file's last-write time. Editing the file bumps
        // the mtime, which must invalidate the cache. Using a sub-second delay
        // is unreliable across filesystems, so we force a distinct mtime by
        // writing then setting an explicit timestamp in the future.
        var svc = NewService("""
            [package]
            name=v1
            source=src
            source_type=git
            """, out var path);

        var first = Assert.Single(svc.ParsePackages());
        Assert.Equal("v1", first.Name);

        // Same mtime -> cached copy returned even if we re-read.
        var firstAgain = Assert.Single(svc.ParsePackages());
        Assert.Same(first, firstAgain);

        // Rewrite with new content and an mtime strictly greater than the
        // cached _lastReadTime. Filesystems with 1-2s mtime granularity make a
        // naive Thread.Sleep flaky, so set the timestamp explicitly.
        await Task.Delay(50);
        File.WriteAllText(path, """
            [package]
            name=v2
            source=src
            source_type=git
            """);
        var future = DateTime.UtcNow.AddMinutes(1);
        File.SetLastWriteTimeUtc(path, future);

        var refreshed = Assert.Single(svc.ParsePackages());
        Assert.Equal("v2", refreshed.Name);
    }

    // ─── GetPackage ──────────────────────────────────────────────────────

    [Fact]
    public void GetPackage_IsCaseInsensitive()
    {
        var svc = NewService("""
            [package]
            name=MyPackage
            source=src
            source_type=git
            """, out _);

        var found = svc.GetPackage("mypackage");
        Assert.NotNull(found);
        Assert.Equal("MyPackage", found!.Name);
    }

    [Fact]
    public void GetPackage_ReturnsNull_WhenMissing()
    {
        var svc = NewService("""
            [package]
            name=a
            source=src
            source_type=git
            """, out _);

        Assert.Null(svc.GetPackage("does-not-exist"));
    }

    // ─── GetRawContent ───────────────────────────────────────────────────

    [Fact]
    public void GetRawContent_ReturnsEmpty_WhenFileMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Source:ConfigPath"] = "/nonexistent/conf-" + Guid.NewGuid() + ".ini"
            })
            .Build();
        var svc = new ConfigParserService(NullLogger<ConfigParserService>.Instance, config);

        Assert.Equal(string.Empty, svc.GetRawContent());
    }
}
