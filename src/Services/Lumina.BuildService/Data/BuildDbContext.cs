using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Lumina.Shared.Security;
using MassTransit;
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
    public DbSet<BuildProject> BuildProjects => Set<BuildProject>();
    public DbSet<ProjectWebhookDelivery> ProjectWebhookDeliveries => Set<ProjectWebhookDelivery>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<BuildJob> BuildJobs => Set<BuildJob>();
    public DbSet<BuildArtifact> BuildArtifacts => Set<BuildArtifact>();
    public DbSet<BuildStepRun> BuildStepRuns => Set<BuildStepRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity(entity => entity.ToTable("inbox_state", "build"));
        modelBuilder.AddOutboxMessageEntity(entity => entity.ToTable("outbox_message", "build"));
        modelBuilder.AddOutboxStateEntity(entity => entity.ToTable("outbox_state", "build"));

        var protector = _secretProtector;

        modelBuilder.Entity<BuildProject>(entity =>
        {
            entity.ToTable("build_projects", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.GitRepoUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.GitBranch).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ManifestPath).IsRequired().HasMaxLength(512);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GitUsername).HasMaxLength(256);
            entity.Property(e => e.UpdatedAt).IsConcurrencyToken();
            entity.Property(e => e.WebhookSecret).IsRequired().HasColumnType("text");
            entity.Property(e => e.GitToken).HasColumnType("text");

            if (protector != null)
            {
                entity.Property(e => e.WebhookSecret).HasConversion(
                    value => protector.Protect(value)!,
                    value => protector.Unprotect(value)!);
                entity.Property(e => e.GitToken).HasConversion(
                    value => protector.Protect(value),
                    value => protector.Unprotect(value));
            }
        });

        modelBuilder.Entity<Pipeline>(entity =>
        {
            entity.ToTable("pipelines", "build", table => table.HasCheckConstraint(
                "CK_pipelines_project_package_binding",
                "(\"BuildProjectId\" IS NULL AND \"PackageId\" IS NULL) OR " +
                "(\"BuildProjectId\" IS NOT NULL AND \"PackageId\" IS NOT NULL)"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GitUsername).HasMaxLength(256);
            entity.Property(e => e.Tags).HasColumnType("text[]");
            entity.Property(e => e.TriggerPaths).HasColumnType("text[]");
            entity.Property(e => e.TargetDistribution).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TargetRelease).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TargetArchitecture).IsRequired().HasMaxLength(64);
            entity.Property(e => e.BuildProfile).IsRequired().HasMaxLength(128);
            entity.Property(e => e.UpdatedAt).IsConcurrencyToken();
            entity.Property(e => e.PackageId).HasMaxLength(128);
            entity.HasIndex(e => new { e.BuildProjectId, e.PackageId })
                .IsUnique()
                .HasFilter("\"BuildProjectId\" IS NOT NULL AND \"PackageId\" IS NOT NULL");

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
            entity.HasOne(e => e.BuildProject)
                .WithMany(e => e.Pipelines)
                .HasForeignKey(e => e.BuildProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectWebhookDelivery>(entity =>
        {
            entity.ToTable("project_webhook_deliveries", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProviderDeliveryId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.RepositoryUrl).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.CommitSha).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Branch).IsRequired().HasMaxLength(256);
            entity.Property(e => e.ChangedPaths).HasColumnType("text[]");
            entity.Property(e => e.CommitAuthor).HasMaxLength(256);
            entity.Property(e => e.CommitMessage).HasMaxLength(2048);
            entity.Property(e => e.SnapshotStoragePath).HasMaxLength(1024);
            entity.Property(e => e.SnapshotSha256).HasMaxLength(64);
            entity.Property(e => e.DispatchPlanJson).HasColumnType("jsonb");
            entity.Property(e => e.ManifestSha256).HasMaxLength(64);
            entity.Property(e => e.FailureCode).HasMaxLength(64);
            entity.HasIndex(e => new { e.BuildProjectId, e.ProviderDeliveryId }).IsUnique();
            entity.HasIndex(e => new { e.Status, e.UpdatedAt });
            entity.HasOne(e => e.BuildProject)
                .WithMany(e => e.WebhookDeliveries)
                .HasForeignKey(e => e.BuildProjectId)
                .OnDelete(DeleteBehavior.Cascade);
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
            entity.ToTable("build_jobs", "build", table => table.HasCheckConstraint(
                "CK_build_jobs_project_dispatch_binding",
                "(\"ProjectWebhookDeliveryId\" IS NULL AND \"ProjectPackageId\" IS NULL AND \"ProjectStageOrder\" IS NULL) OR " +
                "(\"ProjectWebhookDeliveryId\" IS NOT NULL AND \"ProjectPackageId\" IS NOT NULL AND \"ProjectStageOrder\" IS NOT NULL AND \"ProjectStageOrder\" >= 0)"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SpecName).IsRequired().HasMaxLength(256);
            entity.Property(e => e.TriggeredBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(256);
            entity.HasIndex(e => new { e.PipelineId, e.IdempotencyKey })
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.Property(e => e.CommitSha).HasMaxLength(64);
            entity.Property(e => e.Branch).HasMaxLength(256);
            entity.Property(e => e.CommitMessage).HasMaxLength(2048);
            entity.Property(e => e.CommitAuthor).HasMaxLength(256);
            entity.Property(e => e.TargetDistribution).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TargetRelease).IsRequired().HasMaxLength(64);
            entity.Property(e => e.TargetArchitecture).IsRequired().HasMaxLength(64);
            entity.Property(e => e.BuildProfile).IsRequired().HasMaxLength(128);
            entity.Property(e => e.RunnerImageReference).HasMaxLength(512);
            entity.Property(e => e.RunnerImageDigest).HasMaxLength(512);
            entity.Property(e => e.ProjectPackageId).HasMaxLength(128);
            entity.Property(e => e.LeaseOwner).HasMaxLength(128);
            entity.HasIndex(e => new { e.Status, e.LeaseExpiresAt });
            entity.HasIndex(e => new { e.ProjectWebhookDeliveryId, e.ProjectPackageId })
                .IsUnique()
                .HasFilter("\"ProjectWebhookDeliveryId\" IS NOT NULL AND \"ProjectPackageId\" IS NOT NULL");
            entity.HasOne(e => e.ProjectWebhookDelivery)
                .WithMany(e => e.BuildJobs)
                .HasForeignKey(e => e.ProjectWebhookDeliveryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.Artifacts).WithOne(e => e.BuildJob).HasForeignKey(e => e.BuildJobId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.StepRuns).WithOne(e => e.BuildJob).HasForeignKey(e => e.BuildJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BuildStepRun>(entity =>
        {
            entity.ToTable("build_step_runs", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Configuration).HasColumnType("hstore");
            entity.Property(e => e.Error).HasMaxLength(2048);
            entity.HasIndex(e => new { e.BuildJobId, e.Order }).IsUnique();
        });

        modelBuilder.Entity<BuildArtifact>(entity =>
        {
            entity.ToTable("build_artifacts", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(512);
            entity.Property(e => e.SigningKeyFingerprint).HasMaxLength(64);
        });
    }
}
