using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KaleContentOps.Data;

/// <summary>
/// Design-time factory for `dotnet ef` tooling (migrations add / script).
/// Used by the EF tools INSTEAD of running Program.cs, so generating a migration
/// never executes the startup Migrate()/seeding block as a side effect.
/// The connection string below is never connected to during `migrations add`
/// (it is a placeholder; runtime always uses the configured connection string).
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=DesignTimeOnly;Trusted_Connection=True;");
        return new AppDbContext(optionsBuilder.Options);
    }
}
