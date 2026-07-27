using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Data;

public class SourceDbContext : DbContext
{
    public SourceDbContext(DbContextOptions<SourceDbContext> options) : base(options) { }

    public DbSet<SourceJob> SourceJobs => Set<SourceJob>();
    public DbSet<PackageDefinition> PackageDefinitions => Set<PackageDefinition>();
    public DbSet<PackageRevision> PackageRevisions => Set<PackageRevision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PackageDefinition>(entity =>
        {
            entity.ToTable("package_definitions", "source");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ActiveRevisionNumber).IsConcurrencyToken();
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.HasMany(e => e.Revisions)
                .WithOne(e => e.PackageDefinition)
                .HasForeignKey(e => e.PackageDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PackageRevision>(entity =>
        {
            entity.ToTable("package_revisions", "source");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.SourceReference).HasMaxLength(256);
            entity.Property(e => e.ExpectedSha256).HasMaxLength(64);
            entity.Property(e => e.SpecPath).HasMaxLength(512);
            entity.Property(e => e.BuildImage).HasMaxLength(256);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.HasIndex(e => new { e.PackageDefinitionId, e.RevisionNumber }).IsUnique();
        });

        modelBuilder.Entity<SourceJob>(entity =>
        {
            entity.ToTable("source_jobs", "source");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PackageName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.SourceUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.SourceBranch).HasMaxLength(256);
            entity.Property(e => e.ExpectedSha256).HasMaxLength(64);
            entity.Property(e => e.ResolvedRevision).HasMaxLength(128);
            entity.Property(e => e.ResolvedUrl).HasMaxLength(2048);
            entity.Property(e => e.StoragePath).HasMaxLength(1024);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4096);
            entity.Property(e => e.LeaseOwner).HasMaxLength(128).IsConcurrencyToken();
            entity.HasIndex(e => e.PackageName);
            entity.HasOne(e => e.PackageRevision)
                .WithMany(e => e.SourceJobs)
                .HasForeignKey(e => e.PackageRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => e.PackageRevisionId);
            entity.HasIndex(e => new { e.Status, e.LeaseExpiresAt, e.CreatedAt });
        });
    }
}
