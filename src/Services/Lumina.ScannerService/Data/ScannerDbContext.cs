using Lumina.Shared.Models;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ScannerService.Data;

public class ScannerDbContext : DbContext
{
    public ScannerDbContext(DbContextOptions<ScannerDbContext> options) : base(options) { }

    public DbSet<CveReport> CveReports => Set<CveReport>();
    public DbSet<Vulnerability> Vulnerabilities => Set<Vulnerability>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("inbox_state", "scanner"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("outbox_message", "scanner"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("outbox_state", "scanner"));

        modelBuilder.Entity<CveReport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ScannerType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ArtifactSha256).HasMaxLength(64);
            entity.HasIndex(e => e.ArtifactId).IsUnique();
            entity.Ignore(e => e.Artifact); // Artifact lives in build-service DB, no FK here
            entity.HasMany(e => e.Vulnerabilities)
                .WithOne()
                .HasForeignKey(v => v.CveReportId);
        });

        modelBuilder.Entity<Vulnerability>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CveId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => e.CveId);
        });
    }
}
