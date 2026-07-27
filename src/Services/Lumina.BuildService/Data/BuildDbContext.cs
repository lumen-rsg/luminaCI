using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Lumina.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Data;

public class BuildDbContext : DbContext
{
    // Encrypts/decrypts secret columns transparently via EF value converters.
    // Nullable so design-time tooling (and any context built without DI) still
    // constructs the model — converters are simply skipped when it is absent.
    private readonly ISecretProtector? _secretProtector;

    public BuildDbContext(DbContextOptions<BuildDbContext> options, ISecretProtector? secretProtector = null)
        : base(options)
    {
        _secretProtector = secretProtector;
    }

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<BuildJob> BuildJobs => Set<BuildJob>();
    public DbSet<BuildArtifact> BuildArtifacts => Set<BuildArtifact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var protector = _secretProtector;

        modelBuilder.Entity<Pipeline>(entity =>
        {
            entity.ToTable("pipelines", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GitUsername).HasMaxLength(256);
            entity.Property(e => e.Tags).HasColumnType("text[]");
            entity.Property(e => e.UpdatedAt).IsConcurrencyToken();

            // Encrypted secret columns. Stored as `text` because the ciphertext
            // (Base64 of nonce|ciphertext|tag with an "enc1:" prefix) is variable
            // length and unbounded; the legacy 512-char limit on GitToken would
            // silently truncate longer tokens once encrypted.
            entity.Property(e => e.WebhookSecret).HasColumnType("text");
            entity.Property(e => e.GitToken).HasColumnType("text");

            // Encrypt at rest on write, transparently decrypt on read. Legacy
            // plaintext rows pass through Unprotect unchanged and get re-encrypted
            // on their next save (see AesSecretProtector.Unprotect).
            if (protector != null)
            {
                entity.Property(e => e.WebhookSecret).HasConversion(
                    v => protector.Protect(v),
                    v => protector.Unprotect(v));
                entity.Property(e => e.GitToken).HasConversion(
                    v => protector.Protect(v),
                    v => protector.Unprotect(v));
            }

            entity.HasMany(e => e.Steps).WithOne(e => e.Pipeline).HasForeignKey(e => e.PipelineId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PipelineStep>(entity =>
        {
            entity.ToTable("pipeline_steps", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Configuration).HasColumnType("hstore");
        });

        modelBuilder.Entity<BuildJob>(entity =>
        {
            entity.ToTable("build_jobs", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SpecName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.TriggeredBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CommitSha).HasMaxLength(64);
            entity.Property(e => e.Branch).HasMaxLength(256);
            entity.Property(e => e.CommitMessage).HasMaxLength(2048);
            entity.Property(e => e.CommitAuthor).HasMaxLength(256);
            entity.HasMany(e => e.Artifacts).WithOne(e => e.BuildJob).HasForeignKey(e => e.BuildJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BuildArtifact>(entity =>
        {
            entity.ToTable("build_artifacts", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
        });
    }
}
