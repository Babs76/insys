using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Health.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Health.Infrastructure.Persistence;

/// <summary>The Health module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class HealthDbContext(DbContextOptions<HealthDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "health";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => HealthStaticData.Configure(modelBuilder);
}
