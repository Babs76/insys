using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Party.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Party.Infrastructure.Persistence;

/// <summary>The Party module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class PartyDbContext(DbContextOptions<PartyDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "party";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => PartyStaticData.Configure(modelBuilder);
}
