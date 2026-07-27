using Lumina.RepositoryService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumina.RepositoryService.Tests;

public sealed class RepositoryManagerServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"lumina-repository-tests-{Guid.NewGuid():N}");
    private readonly RepositoryManagerService _manager;

    public RepositoryManagerServiceTests()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Repository:BasePath"] = _root
            })
            .Build();
        _manager = new RepositoryManagerService(
            NullLogger<RepositoryManagerService>.Instance, configuration);
    }

    [Fact]
    public async Task Staging_IsPrivateAndRejectsNonBasename()
    {
        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());

        Assert.StartsWith(Path.Combine(_root, ".staging"), staging);
        await Assert.ThrowsAsync<Lumina.Shared.Errors.ValidationException>(() =>
            _manager.WriteStagedRpmAsync(
                staging, "../escape.rpm", new MemoryStream([1, 2, 3])));
    }

    [Fact]
    public void ObjectDownloadPath_IsConfinedAndCannotOverwrite()
    {
        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());
        var path = _manager.GetStagedRpmPath(staging, "pkg-1.0-1.noarch.rpm");

        Assert.StartsWith(staging, path);
        Assert.Throws<Lumina.Shared.Errors.ValidationException>(() =>
            _manager.GetStagedRpmPath(staging, "../escape.rpm"));

        File.WriteAllText(path, "existing");
        Assert.Throws<Lumina.Shared.Errors.ConflictException>(() =>
            _manager.GetStagedRpmPath(staging, "pkg-1.0-1.noarch.rpm"));
    }

    [Fact]
    public void Rollback_RestoresPreviousMetadataAndRemovesCandidate()
    {
        var live = _manager.EnsureRepoDir("stable", "noarch");
        var oldRepodata = Path.Combine(live, "repodata");
        Directory.CreateDirectory(oldRepodata);
        File.WriteAllText(Path.Combine(oldRepodata, "repomd.xml"), "old");

        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());
        var snapshot = Path.Combine(staging, "snapshot");
        Directory.CreateDirectory(Path.Combine(snapshot, "repodata"));
        File.WriteAllText(Path.Combine(snapshot, "pkg-1.0-1.noarch.rpm"), "rpm");
        File.WriteAllText(Path.Combine(snapshot, "repodata", "repomd.xml"), "new");

        var publication = _manager.CommitStagedPublication(
            "stable", "noarch", snapshot, "pkg-1.0-1.noarch.rpm");

        Assert.Equal("new", File.ReadAllText(Path.Combine(live, "repodata", "repomd.xml")));
        Assert.True(File.Exists(Path.Combine(live, "pkg-1.0-1.noarch.rpm")));

        _manager.RollbackPublication(publication);

        Assert.Equal("old", File.ReadAllText(Path.Combine(live, "repodata", "repomd.xml")));
        Assert.False(File.Exists(Path.Combine(live, "pkg-1.0-1.noarch.rpm")));
    }

    [Fact]
    public void Complete_KeepsNewMetadataAndRemovesPreviousMetadata()
    {
        var live = _manager.EnsureRepoDir("stable", "noarch");
        Directory.CreateDirectory(Path.Combine(live, "repodata"));
        File.WriteAllText(Path.Combine(live, "repodata", "repomd.xml"), "old");

        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());
        var snapshot = Path.Combine(staging, "snapshot");
        Directory.CreateDirectory(Path.Combine(snapshot, "repodata"));
        File.WriteAllText(Path.Combine(snapshot, "pkg-2.0-1.noarch.rpm"), "rpm");
        File.WriteAllText(Path.Combine(snapshot, "repodata", "repomd.xml"), "new");

        var publication = _manager.CommitStagedPublication(
            "stable", "noarch", snapshot, "pkg-2.0-1.noarch.rpm");
        _manager.CompletePublication(publication);

        Assert.Equal("new", File.ReadAllText(Path.Combine(live, "repodata", "repomd.xml")));
        Assert.False(Directory.Exists(publication.PreviousSnapshotPath));
    }

    [Fact]
    public void Recovery_RollsBackFilesystemWhenDatabaseDidNotCommit()
    {
        var live = _manager.EnsureRepoDir("stable", "noarch");
        Directory.CreateDirectory(Path.Combine(live, "repodata"));
        File.WriteAllText(Path.Combine(live, "repodata", "repomd.xml"), "old");

        var packageId = Guid.NewGuid();
        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());
        var snapshot = Path.Combine(staging, "snapshot");
        Directory.CreateDirectory(Path.Combine(snapshot, "repodata"));
        File.WriteAllText(Path.Combine(snapshot, "pkg-3.0-1.noarch.rpm"), "rpm");
        File.WriteAllText(Path.Combine(snapshot, "repodata", "repomd.xml"), "new");
        _manager.WritePublicationJournal(
            staging, packageId, "stable", "noarch", "pkg-3.0-1.noarch.rpm");
        _manager.CommitStagedPublication(
            "stable", "noarch", snapshot, "pkg-3.0-1.noarch.rpm");

        var journal = Assert.Single(_manager.ReadPublicationJournals()).Journal;
        _manager.RecoverPublication(journal, databaseCommitted: false);

        Assert.Equal("old", File.ReadAllText(Path.Combine(live, "repodata", "repomd.xml")));
        Assert.False(File.Exists(Path.Combine(live, "pkg-3.0-1.noarch.rpm")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Recovery_FinalizesFilesystemWhenDatabaseCommitted()
    {
        var live = _manager.EnsureRepoDir("stable", "noarch");
        Directory.CreateDirectory(Path.Combine(live, "repodata"));
        File.WriteAllText(Path.Combine(live, "repodata", "repomd.xml"), "old");

        var packageId = Guid.NewGuid();
        var staging = _manager.CreatePublicationStagingDirectory(Guid.NewGuid());
        var snapshot = Path.Combine(staging, "snapshot");
        Directory.CreateDirectory(Path.Combine(snapshot, "repodata"));
        File.WriteAllText(Path.Combine(snapshot, "pkg-4.0-1.noarch.rpm"), "rpm");
        File.WriteAllText(Path.Combine(snapshot, "repodata", "repomd.xml"), "new");
        _manager.WritePublicationJournal(
            staging, packageId, "stable", "noarch", "pkg-4.0-1.noarch.rpm");
        _manager.CommitStagedPublication(
            "stable", "noarch", snapshot, "pkg-4.0-1.noarch.rpm");

        var journal = Assert.Single(_manager.ReadPublicationJournals()).Journal;
        _manager.RecoverPublication(journal, databaseCommitted: true);

        Assert.Equal("new", File.ReadAllText(Path.Combine(live, "repodata", "repomd.xml")));
        Assert.True(File.Exists(Path.Combine(live, "pkg-4.0-1.noarch.rpm")));
        Assert.False(Directory.Exists(staging));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
