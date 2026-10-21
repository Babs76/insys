using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Analytics.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Analytics.Infrastructure.Persistence;

/// <summary>The Analytics module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "analytics";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => AnalyticsStaticData.Configure(modelBuilder);
}
