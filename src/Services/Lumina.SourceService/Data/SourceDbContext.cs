using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SourceService.Data;

public class SourceDbContext : DbContext
{
    public SourceDbContext(DbContextOptions<SourceDbContext> options) : base(options) { }

    public DbSet<SourceJob> SourceJobs => Set<SourceJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceJob>(entity =>
        {
            entity.ToTable("source_jobs", "source");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PackageName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.SourceUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.SourceBranch).HasMaxLength(256);
            entity.Property(e => e.StoragePath).HasMaxLength(1024);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4096);
            entity.Property(e => e.LeaseOwner).HasMaxLength(128).IsConcurrencyToken();
            entity.HasIndex(e => e.PackageName);
            entity.HasIndex(e => new { e.Status, e.LeaseExpiresAt, e.CreatedAt });
        });
    }
}
