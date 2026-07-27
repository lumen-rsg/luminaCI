using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.RepositoryService.Data;

public class RepositoryDbContext : DbContext
{
    public RepositoryDbContext(DbContextOptions<RepositoryDbContext> options) : base(options) { }

    public DbSet<PackageRepository> Repositories => Set<PackageRepository>();
    public DbSet<Package> Packages => Set<Package>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PackageRepository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.BasePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Arch).IsRequired().HasMaxLength(20);
            entity.HasMany(e => e.Packages)
                .WithOne(p => p.Repository)
                .HasForeignKey(p => p.RepositoryId);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<Package>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.SigningKeyFingerprint).HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => new { e.RepositoryId, e.Name, e.Version, e.Release, e.Arch }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.FileName }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.ArtifactId })
                .IsUnique()
                .HasFilter("\"ArtifactId\" IS NOT NULL");
        });
    }
}
