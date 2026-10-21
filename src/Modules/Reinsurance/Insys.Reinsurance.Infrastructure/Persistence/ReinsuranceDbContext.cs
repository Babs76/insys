using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Reinsurance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Reinsurance.Infrastructure.Persistence;

/// <summary>The Reinsurance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ReinsuranceDbContext(DbContextOptions<ReinsuranceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "reinsurance";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => ReinsuranceStaticData.Configure(modelBuilder);
}
