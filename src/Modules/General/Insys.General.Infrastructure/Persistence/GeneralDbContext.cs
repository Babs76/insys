using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.General.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.General.Infrastructure.Persistence;

/// <summary>The General module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class GeneralDbContext(DbContextOptions<GeneralDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "general";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => GeneralStaticData.Configure(modelBuilder);
}
