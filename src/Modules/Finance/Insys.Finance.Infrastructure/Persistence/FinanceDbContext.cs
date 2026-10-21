using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Finance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Finance.Infrastructure.Persistence;

/// <summary>The Finance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "finance";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => FinanceStaticData.Configure(modelBuilder);
}
