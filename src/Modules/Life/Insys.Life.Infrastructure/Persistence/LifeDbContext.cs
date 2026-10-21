using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Life.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Life.Infrastructure.Persistence;

/// <summary>The Life module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class LifeDbContext(DbContextOptions<LifeDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "life";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => LifeStaticData.Configure(modelBuilder);
}
