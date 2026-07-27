using Lumina.Shared.Models;
using MassTransit;
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
        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("inbox_state", "security"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("outbox_message", "security"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("outbox_state", "security"));

        modelBuilder.Entity<SecurityKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.KeyId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.KeyName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PublicKey).IsRequired();
            entity.HasIndex(e => e.KeyId).IsUnique();
            entity.HasIndex(e => e.IsActive)
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE");
        });

        modelBuilder.Entity<SigningRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ArtifactPath).IsRequired();
            entity.Property(e => e.KeyFingerprint).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ExpectedSha256).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SignedSha256).HasMaxLength(64);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.HasIndex(e => e.ArtifactId).IsUnique();
        });

        modelBuilder.Entity<HashRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Sha256).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Md5).HasMaxLength(64);
            entity.HasIndex(e => e.ArtifactId).IsUnique();
        });
    }
}
