using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Payments.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Payments.Infrastructure.Persistence;

/// <summary>The Payments module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "payment";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => PaymentsStaticData.Configure(modelBuilder);
}
