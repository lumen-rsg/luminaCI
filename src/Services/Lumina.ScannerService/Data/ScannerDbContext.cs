using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ScannerService.Data;

public class ScannerDbContext : DbContext
{
    public ScannerDbContext(DbContextOptions<ScannerDbContext> options) : base(options) { }

    public DbSet<CveReport> CveReports => Set<CveReport>();
    public DbSet<Vulnerability> Vulnerabilities => Set<Vulnerability>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CveReport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ScannerType).IsRequired().HasMaxLength(20);
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