using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SecurityService.Data;

public class SecurityDbContext : DbContext
{
    public SecurityDbContext(DbContextOptions<SecurityDbContext> options) : base(options) { }

    public DbSet<SecurityKey> SecurityKeys => Set<SecurityKey>();
    public DbSet<SigningRequest> SigningRequests => Set<SigningRequest>();
    public DbSet<HashRecord> HashRecords => Set<HashRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SecurityKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.KeyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.HasIndex(e => e.KeyId).IsUnique();
        });

        modelBuilder.Entity<SigningRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ArtifactPath).IsRequired();
            entity.Property(e => e.SignaturePath).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<HashRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Sha256).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Md5).HasMaxLength(64);
            entity.HasIndex(e => e.FileName);
        });
    }
}