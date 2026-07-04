using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lumina.ScannerService.Data;

// Lets `dotnet ef migrations add` build the context WITHOUT running Program.cs.
// The connection string is a placeholder — EF Core only reflects the model when
// scaffolding; it never opens the connection.
public class ScannerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ScannerDbContext>
{
    public ScannerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ScannerDbContext>()
            .UseNpgsql("Host=localhost;Database=lumina_ci;Username=lumina;Password=design")
            .Options;
        return new ScannerDbContext(options);
    }
}
