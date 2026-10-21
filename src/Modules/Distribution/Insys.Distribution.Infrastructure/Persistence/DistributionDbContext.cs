using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Distribution.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Distribution.Infrastructure.Persistence;

/// <summary>The Distribution module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class DistributionDbContext(DbContextOptions<DistributionDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "commission";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => DistributionStaticData.Configure(modelBuilder);
}
