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
    public void GroupPromotion_ExchangesWholeRepositoryAndRollsBackBothArchitectures()
    {
        var arm = _manager.EnsureRepoDir("grouped", "aarch64");
        var noarch = _manager.EnsureRepoDir("grouped", "noarch");
        Directory.CreateDirectory(Path.Combine(arm, "repodata"));
        Directory.CreateDirectory(Path.Combine(noarch, "repodata"));
        File.WriteAllText(Path.Combine(arm, "repodata", "repomd.xml"), "old-arm");
        File.WriteAllText(Path.Combine(noarch, "repodata", "repomd.xml"), "old-noarch");

        var setId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var staging = _manager.CreatePublicationStagingDirectory(repositoryId);
        var snapshot = _manager.CreateRepositorySnapshot("grouped", staging);
        var armCandidate = _manager.GetRepositorySnapshotRpmPath(
            snapshot, "aarch64", "kernel-2-1.aarch64.rpm");
        var noarchCandidate = _manager.GetRepositorySnapshotRpmPath(
            snapshot, "noarch", "firmware-2-1.noarch.rpm");
        File.WriteAllText(armCandidate, "new-arm");
        File.WriteAllText(noarchCandidate, "new-noarch");
        File.WriteAllText(Path.Combine(snapshot, "aarch64", "repodata", "repomd.xml"), "new-arm-metadata");
        File.WriteAllText(Path.Combine(snapshot, "noarch", "repodata", "repomd.xml"), "new-noarch-metadata");
        PromotionCandidateFile[] candidates =
        [
            new("aarch64", Path.GetFileName(armCandidate)),
            new("noarch", Path.GetFileName(noarchCandidate))
        ];
        _manager.WritePromotionJournal(staging, setId, repositoryId, "grouped", candidates);

        var publication = _manager.CommitStagedRepository("grouped", snapshot, candidates);
        var journal = Assert.Single(_manager.ReadPromotionJournals()).Journal;

        Assert.True(_manager.LiveContainsPromotionCandidates(journal));
        Assert.Equal("new-arm-metadata", File.ReadAllText(Path.Combine(arm, "repodata", "repomd.xml")));
        Assert.Equal("new-noarch-metadata", File.ReadAllText(Path.Combine(noarch, "repodata", "repomd.xml")));

        _manager.RollbackStagedRepository(publication);

        Assert.False(_manager.LiveContainsPromotionCandidates(journal));
        Assert.Equal("old-arm", File.ReadAllText(Path.Combine(arm, "repodata", "repomd.xml")));
        Assert.Equal("old-noarch", File.ReadAllText(Path.Combine(noarch, "repodata", "repomd.xml")));
    }

    [Fact]
    public void PromotionJournal_RejectsChangedStagingIdentity()
    {
        var repositoryId = Guid.NewGuid();
        var staging = _manager.CreatePublicationStagingDirectory(repositoryId);
        _manager.WritePromotionJournal(
            staging,
            Guid.NewGuid(),
            repositoryId,
            "stable",
            [new PromotionCandidateFile("noarch", "pkg-1-1.noarch.rpm")]);
        var journalPath = Path.Combine(staging, "promotion.json");
        var json = File.ReadAllText(journalPath).Replace(
            staging,
            Path.Combine(_root, ".staging", repositoryId.ToString("N"), "different"),
            StringComparison.Ordinal);
        File.WriteAllText(journalPath, json);

        Assert.Throws<InvalidOperationException>(() => _manager.ReadPromotionJournals());
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
