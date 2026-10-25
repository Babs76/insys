/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
            approval.Property(a => a.ProductCode).HasMaxLength(15);
            approval.Property(a => a.OrgNodeCode).HasMaxLength(15);
            approval.HasKey(a => new { a.TenantId, a.Id });
            approval.HasAuditColumns();
            approval.Ignore(a => a.DomainEvents);
            approval.Ignore(a => a.HasChanges);
            approval.Property(a => a.BusinessRefType).HasMaxLength(30);
            approval.Property(a => a.ApprovalType).HasMaxLength(30);
            approval.Property(a => a.Status).HasConversion(new UpperCaseEnumConverter<ApprovalStatus>()).HasMaxLength(12);
            approval.Property(a => a.Decision).HasMaxLength(10);
            approval.Property(a => a.NotAppliedBy).HasMaxLength(100);
            approval.Property(a => a.Amount).HasColumnName("authority_limit").HasPrecision(18, 2);
            approval.Property(a => a.Currency).HasColumnType("char(3)");
            approval.Property(a => a.SubjectHash).HasMaxLength(64);
            approval.Property(a => a.RequiredPermission).HasMaxLength(80);
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
