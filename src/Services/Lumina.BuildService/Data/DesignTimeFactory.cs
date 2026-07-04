using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lumina.BuildService.Data;

// Lets `dotnet ef migrations add` build the context WITHOUT running Program.cs
// (which would otherwise require RabbitMQ/Redis/secrets to be reachable at
// design time). The connection string is a placeholder — EF Core only reflects
// the model when scaffolding; it never opens the connection.
//
// The protector is deliberately omitted: with no ISecretProtector, the
// WebhookSecret/GitToken value converters are skipped, so the migration treats
// them as plain `text` — matching the on-disk column type today.
public class BuildDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BuildDbContext>
{
    public BuildDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BuildDbContext>()
            .UseNpgsql("Host=localhost;Database=lumina_ci;Username=lumina;Password=design")
            .Options;
        return new BuildDbContext(options);
    }
}
