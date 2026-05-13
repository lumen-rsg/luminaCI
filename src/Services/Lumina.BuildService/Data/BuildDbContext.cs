using Lumina.Shared.Models;
using Lumina.Shared.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Lumina.BuildService.Data;

public class BuildDbContext : DbContext
{
    public BuildDbContext(DbContextOptions<BuildDbContext> options) : base(options) { }

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<PipelineStep> PipelineSteps => Set<PipelineStep>();
    public DbSet<BuildJob> BuildJobs => Set<BuildJob>();
    public DbSet<BuildArtifact> BuildArtifacts => Set<BuildArtifact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pipeline>(entity =>
        {
            entity.ToTable("pipelines", "build");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
            entity.Property(e => e.GitUsername).HasMaxLength(256);
            entity.Property(e => e.GitToken).HasMaxLength(512);
            entity.Property(e => e.Tags).HasColumnType("text[]");
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