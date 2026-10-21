using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rules.Infrastructure.Persistence;

/// <summary>The Rules module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class RulesDbContext(DbContextOptions<RulesDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "rules";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => RulesStaticData.Configure(modelBuilder);
}
