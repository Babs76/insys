using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Rating.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rating.Infrastructure.Persistence;

/// <summary>The Rating module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class RatingDbContext(DbContextOptions<RatingDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "rating";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => RatingStaticData.Configure(modelBuilder);
}
