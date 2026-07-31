using Lumina.Shared.Models;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Data;

public class RepositoryDbContext : DbContext
{
    public RepositoryDbContext(DbContextOptions<RepositoryDbContext> options) : base(options) { }

    public DbSet<PackageRepository> Repositories => Set<PackageRepository>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<RepositoryPromotionSet> PromotionSets => Set<RepositoryPromotionSet>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("inbox_state", "repository"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("outbox_message", "repository"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("outbox_state", "repository"));

        modelBuilder.Entity<PackageRepository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.BasePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Arch).IsRequired().HasMaxLength(20);
            entity.HasMany(e => e.Packages)
                .WithOne(p => p.Repository)
                .HasForeignKey(p => p.RepositoryId);
            entity.HasMany(e => e.PromotionSets)
                .WithOne(p => p.Repository)
                .HasForeignKey(p => p.RepositoryId);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<Package>(entity =>
        {
            entity.ToTable("Packages", table => table.HasCheckConstraint(
                "CK_Packages_candidate_identity",
                "(\"PromotionSetId\" IS NULL AND \"PromotionPackageId\" IS NULL AND \"CandidateObjectName\" IS NULL AND \"Status\" NOT IN ('Candidate', 'RolledBack')) OR " +
                "(\"PromotionSetId\" IS NOT NULL AND \"PromotionPackageId\" IS NOT NULL AND \"CandidateObjectName\" IS NOT NULL AND \"Status\" IN ('Candidate', 'Ready', 'RolledBack'))"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.SigningKeyFingerprint).HasMaxLength(64);
            entity.Property(e => e.CandidateObjectName).HasMaxLength(1024);
            entity.Property(e => e.PromotionPackageId).HasMaxLength(128);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.HasOne(e => e.PromotionSet)
                .WithMany(set => set.Packages)
                .HasForeignKey(e => new { e.PromotionSetId, e.RepositoryId })
                .HasPrincipalKey(set => new { set.Id, set.RepositoryId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.RepositoryId, e.Name, e.Version, e.Release, e.Arch }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.FileName }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.ArtifactId })
                .IsUnique()
                .HasFilter("\"ArtifactId\" IS NOT NULL");
        });

        modelBuilder.Entity<RepositoryPromotionSet>(entity =>
        {
            entity.ToTable("PromotionSets", table => table.HasCheckConstraint(
                "CK_PromotionSets_state",
                "(\"Status\" = 0 AND \"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR " +
                "(\"Status\" = 1 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR " +
                "(\"Status\" = 2 AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NULL) OR " +
                "(\"Status\" = 3 AND \"GateResultSha256\" IS NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND \"PromotedAt\" IS NULL AND ((\"GateJobName\" IS NULL AND \"GateJobUid\" IS NULL) OR (\"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL))) OR " +
                "(\"Status\" IN (4, 5) AND \"GateJobName\" IS NOT NULL AND \"GateJobUid\" IS NOT NULL AND \"GateResultSha256\" IS NOT NULL AND \"GateCompletedAt\" IS NOT NULL AND \"FailureReason\" IS NULL AND \"PromotedAt\" IS NOT NULL)"));
            entity.ToTable("PromotionSets", table => table.HasCheckConstraint(
                "CK_PromotionSets_gate_bundle",
                "((\"GateBundleObjectName\" IS NULL AND \"GateCandidateManifestSha256\" IS NULL AND \"GateBaselineManifestSha256\" IS NULL AND \"GateBundleSha256\" IS NULL AND \"GateBundleSize\" IS NULL AND \"GateBundlePreparedAt\" IS NULL) OR " +
                "(\"GateBundleObjectName\" IS NOT NULL AND \"GateCandidateManifestSha256\" IS NOT NULL AND \"GateBaselineManifestSha256\" IS NOT NULL AND \"GateBundleSha256\" IS NOT NULL AND \"GateBundleSize\" > 0 AND \"GateBundlePreparedAt\" IS NOT NULL)) AND " +
                "(\"Status\" IN (0, 3) OR \"GateBundleObjectName\" IS NOT NULL)"));
            entity.ToTable("PromotionSets", table => table.HasCheckConstraint(
                "CK_PromotionSets_publication",
                "(\"Status\" IN (0, 1, 2, 3) AND \"PromotedRepositoryManifestSha256\" IS NULL AND \"RollbackSnapshotPath\" IS NULL AND \"RolledBackAt\" IS NULL AND \"RolledBackBy\" IS NULL AND \"RollbackReason\" IS NULL) OR " +
                "(\"Status\" = 4 AND \"PromotedRepositoryManifestSha256\" IS NOT NULL AND \"RollbackSnapshotPath\" IS NOT NULL AND \"RolledBackAt\" IS NULL AND \"RolledBackBy\" IS NULL AND \"RollbackReason\" IS NULL) OR " +
                "(\"Status\" = 5 AND \"PromotedRepositoryManifestSha256\" IS NOT NULL AND \"RollbackSnapshotPath\" IS NULL AND \"RolledBackAt\" IS NOT NULL AND \"RolledBackBy\" IS NOT NULL AND \"RollbackReason\" IS NOT NULL)"));
            entity.HasKey(e => e.Id);
            entity.HasAlternateKey(e => new { e.Id, e.RepositoryId });
            entity.Property(e => e.PromotionGroup).IsRequired().HasMaxLength(128);
            entity.Property(e => e.TargetArchitecture).IsRequired().HasMaxLength(64);
            entity.Property(e => e.GateRunnerImageDigest).IsRequired().HasMaxLength(71);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GateJobName).HasMaxLength(63);
            entity.Property(e => e.GateJobUid).HasMaxLength(128);
            entity.Property(e => e.GateResultSha256).HasMaxLength(64);
            entity.Property(e => e.FailureReason).HasMaxLength(2048);
            entity.Property(e => e.GateBundleObjectName).HasMaxLength(1024);
            entity.Property(e => e.GateCandidateManifestSha256).HasMaxLength(64);
            entity.Property(e => e.GateBaselineManifestSha256).HasMaxLength(64);
            entity.Property(e => e.GateBundleSha256).HasMaxLength(64);
            entity.Property(e => e.PromotedRepositoryManifestSha256).HasMaxLength(64);
            entity.Property(e => e.RollbackSnapshotPath).HasMaxLength(1024);
            entity.Property(e => e.RolledBackBy).HasMaxLength(256);
            entity.Property(e => e.RollbackReason).HasMaxLength(2048);
            entity.HasIndex(e => new { e.RepositoryId, e.Status });
            entity.HasIndex(e => new { e.RepositoryId, e.PromotionGroup, e.CreatedAt });
        });
    }
}
