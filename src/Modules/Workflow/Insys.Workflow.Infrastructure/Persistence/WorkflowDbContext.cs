using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Workflow.Domain.Approvals;
using Insys.Workflow.Domain.Processes;
using Insys.Workflow.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Workflow.Infrastructure.Persistence;

/// <summary>The Workflow module's context: the workflow schema only, on the insys_workflow role.</summary>
public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "workflow";

    public override string Schema => SchemaName;

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<ProcessInstance> Processes => Set<ProcessInstance>();

    public DbSet<CaseNote> CaseNotes => Set<CaseNote>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Case notes are kept by business reference rather than by process, so they are added here, not through a navigation.
        foreach (var note in ChangeTracker.Entries<ProcessInstance>().SelectMany(e => e.Entity.NewNotes))
        {
            if (Entry(note).State == EntityState.Detached)
            {
                CaseNotes.Add(note);
            }
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        WorkflowStaticData.Configure(modelBuilder);

        modelBuilder.Entity<Approval>(approval =>
        {
            approval.ToTable("approval");
            approval.HasKey(a => new { a.TenantId, a.Id });
            approval.HasAuditColumns();
            approval.Ignore(a => a.DomainEvents);
            approval.Ignore(a => a.HasChanges);
            approval.Property(a => a.BusinessRefType).HasMaxLength(30);
            approval.Property(a => a.ApprovalType).HasMaxLength(30);
            approval.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<ApprovalStatus>()).HasMaxLength(12);
            approval.Property(a => a.Decision).HasMaxLength(10);
            approval.Property(a => a.Amount).HasColumnName("authority_limit").HasPrecision(18, 2);
            approval.Property(a => a.Currency).HasColumnType("char(3)");
        });

        modelBuilder.Entity<ProcessInstance>(process =>
        {
            process.ToTable("process_instance");
            process.HasKey(p => new { p.TenantId, p.Id });
            process.HasAuditColumns();
            process.Ignore(p => p.DomainEvents);
            process.Ignore(p => p.HasChanges);
            process.Ignore(p => p.NewNotes);
            process.Ignore(p => p.Status);
            process.Ignore(p => p.DefinitionState);
            process.Ignore(p => p.OpenTask);
            process.Property(p => p.ProcessCode).HasMaxLength(20);
            process.Property(p => p.BusinessRefType).HasMaxLength(30);
            process.Property(p => p.State).HasMaxLength(30);
            process.HasMany(p => p.Tasks).WithOne().HasForeignKey(t => new { t.TenantId, t.ProcessInstanceId });
            process.Navigation(p => p.Tasks).UsePropertyAccessMode(PropertyAccessMode.Field);
            process.HasMany(p => p.Timers).WithOne().HasForeignKey(t => new { t.TenantId, t.ProcessInstanceId });
            process.Navigation(p => p.Timers).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ProcessTask>(task =>
        {
            task.ToTable("task");
            task.HasKey(t => new { t.TenantId, t.Id });
            task.HasAuditColumns();
            task.Property(t => t.TaskType).HasMaxLength(30);
            task.Property(t => t.QueueCode).HasMaxLength(20);
            task.Property(t => t.Status).HasConversion(new UpperCaseEnumConverter<Domain.Processes.TaskStatus>()).HasMaxLength(12);
            task.Property(t => t.SlaCode).HasMaxLength(20);
            task.Property(t => t.Outcome).HasMaxLength(20);
        });

        modelBuilder.Entity<ProcessTimer>(timer =>
        {
            timer.ToTable("timer");
            timer.HasKey(t => new { t.TenantId, t.Id });
            timer.HasAuditColumns();
            timer.Ignore(t => t.TaskId);
            timer.Property(t => t.TimerType).HasMaxLength(20);
        });

        modelBuilder.Entity<CaseNote>(note =>
        {
            note.ToTable("case_note");
            note.HasKey(n => new { n.TenantId, n.Id });
            note.HasAuditColumns();
            note.Property(n => n.BusinessRefType).HasMaxLength(30);
        });
    }
}
