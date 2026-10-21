using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Billing.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Billing.Infrastructure.Persistence;

/// <summary>The Billing module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "billing";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => BillingStaticData.Configure(modelBuilder);
}
