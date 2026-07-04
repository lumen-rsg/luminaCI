using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lumina.ApiGateway.Data;

// Lets `dotnet ef migrations add` build the context WITHOUT running Program.cs.
// The connection string is a placeholder — EF Core only reflects the model when
// scaffolding; it never opens the connection.
public class AuthDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=lumina_ci;Username=lumina;Password=design")
            .Options;
        return new AuthDbContext(options);
    }
}
