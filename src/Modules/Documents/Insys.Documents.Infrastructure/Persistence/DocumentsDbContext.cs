using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Documents.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Documents.Infrastructure.Persistence;

/// <summary>The Documents module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "document";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => DocumentsStaticData.Configure(modelBuilder);
}
