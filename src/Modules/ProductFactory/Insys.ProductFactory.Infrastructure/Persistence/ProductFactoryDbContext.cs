using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.ProductFactory.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.ProductFactory.Infrastructure.Persistence;

/// <summary>The ProductFactory module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ProductFactoryDbContext(DbContextOptions<ProductFactoryDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "product";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => ProductFactoryStaticData.Configure(modelBuilder);
}
