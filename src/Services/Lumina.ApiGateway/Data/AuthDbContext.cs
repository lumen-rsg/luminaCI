using Lumina.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Lumina.ApiGateway.Data;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.ToTable("users", "auth");
            entity.Property(e => e.Username).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.Role).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FailedLoginAttempts).HasDefaultValue(0);
            entity.Property(e => e.LockoutCount).HasDefaultValue(0);
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.ToTable("audit_logs", "audit", table =>
            {
                table.HasCheckConstraint(
                    "CK_audit_logs_Phase",
                    "\"Phase\" IN ('Requested', 'Completed', 'Failed')");
                table.HasCheckConstraint(
                    "CK_audit_logs_StatusCode",
                    "\"StatusCode\" IS NULL OR \"StatusCode\" BETWEEN 100 AND 599");
                table.HasCheckConstraint(
                    "CK_audit_logs_PreviousHash",
                    "\"PreviousHash\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "CK_audit_logs_EntryHash",
                    "\"EntryHash\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "CK_audit_logs_Details",
                    "\"Details\" IS JSON OBJECT");
            });
            entity.Property(e => e.Sequence).UseIdentityAlwaysColumn();
            entity.Property(e => e.Action).IsRequired().HasMaxLength(120);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(120);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(240);
            entity.Property(e => e.PerformedBy).IsRequired().HasMaxLength(120);
            entity.Property(e => e.Details).IsRequired().HasColumnType("text");
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.CorrelationId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Phase).IsRequired().HasMaxLength(32);
            entity.Property(e => e.PreviousHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.EntryHash).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.Sequence).IsUnique();
            entity.HasIndex(e => e.EntryHash).IsUnique();
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.PerformedBy);
        });
    }
}
