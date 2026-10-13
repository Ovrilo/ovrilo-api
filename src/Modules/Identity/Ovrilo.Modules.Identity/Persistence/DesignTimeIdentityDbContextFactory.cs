using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;

namespace Ovrilo.Modules.Identity.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeIdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<IdentityDbContext>(IdentityDbContext.Schema));
}
