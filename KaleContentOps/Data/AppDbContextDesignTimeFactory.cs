using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace KaleContentOps.Data;

/// <summary>
/// Design-time factory for `dotnet ef` tooling (dbcontext info / migrations add / script).
///
/// EF tools always prefer an <see cref="IDesignTimeDbContextFactory{TContext}"/> over running
/// Program.cs, so this factory is the ONLY place the tools resolve their DbContext from.
/// It therefore resolves the connection string from the project's normal configuration sources
/// and precedence (appsettings.json, appsettings.{Environment}.json, user secrets in Development,
/// environment variables) using the same key the runtime uses
/// ("ConnectionStrings:DefaultConnection"), and fails fast when it is not configured.
/// Keeping a factory here also means design-time commands never execute Program.cs, whose
/// startup pipeline runs Database.Migrate() and seeding as a side effect.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string ConnectionStringKey = "DefaultConnection";

    public AppDbContext CreateDbContext(string[] args)
    {
        // Same environment resolution as WebApplication.CreateBuilder (falls back to Production).
        // Note: the EF tools themselves run the design-time process with
        // ASPNETCORE_ENVIRONMENT=Development unless another environment is set, so by default
        // design-time commands resolve appsettings.Development.json like a local dev run.
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(ResolveProjectDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(AppDbContextDesignTimeFactory).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString(ConnectionStringKey);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Design-time connection string 'ConnectionStrings:{ConnectionStringKey}' is not configured " +
                $"for environment '{environment}'. Set it in appsettings.Development.json, user secrets, " +
                $"or the 'ConnectionStrings__{ConnectionStringKey}' environment variable before running EF commands " +
                "(for example: set ASPNETCORE_ENVIRONMENT=Development).");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(connectionString);
        return new AppDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Locates the project directory (the folder containing the .csproj) by walking up from the
    /// build output, so configuration files are always read from the source project regardless
    /// of the working directory `dotnet ef` happens to be invoked from.
    /// </summary>
    private static string ResolveProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.csproj").Any())
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
