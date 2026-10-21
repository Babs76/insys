using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>The Workflow module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "workflow";

    public override string Schema => SchemaName;

    protected override void ConfigureModel(ModelBuilder modelBuilder) => WorkflowStaticData.Configure(modelBuilder);
}
