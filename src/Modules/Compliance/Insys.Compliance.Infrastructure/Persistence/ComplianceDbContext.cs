using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Compliance.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Compliance.Infrastructure.Persistence;

/// <summary>The Compliance module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "reporting";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => ComplianceStaticData.Configure(modelBuilder);
}
