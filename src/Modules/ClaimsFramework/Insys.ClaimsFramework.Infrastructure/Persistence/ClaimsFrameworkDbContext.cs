using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.ClaimsFramework.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ClaimsFramework.Infrastructure.Persistence;

/// <summary>The ClaimsFramework module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ClaimsFrameworkDbContext(DbContextOptions<ClaimsFrameworkDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "claims";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => ClaimsFrameworkStaticData.Configure(modelBuilder);
}
