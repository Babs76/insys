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
using Insys.Workflow.Domain.Cases;
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

    public DbSet<ServiceCase> Cases => Set<ServiceCase>();

    public DbSet<CaseHistoryEntry> CaseHistory => Set<CaseHistoryEntry>();

    public DbSet<CaseMessage> CaseMessages => Set<CaseMessage>();

    public DbSet<CaseRedress> CaseRedress => Set<CaseRedress>();

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

        // A case's history and messages are append-only rows, added here rather than loaded with the case.
        foreach (var entry in ChangeTracker.Entries<ServiceCase>().SelectMany(e => e.Entity.NewHistory)
                     .Concat(ChangeTracker.Entries<CaseRedress>().SelectMany(e => e.Entity.NewHistory)).ToList())
        {
            if (Entry(entry).State == EntityState.Detached)
            {
                CaseHistory.Add(entry);
            }
        }

        foreach (var message in ChangeTracker.Entries<ServiceCase>().SelectMany(e => e.Entity.NewMessages).ToList())
        {
            if (Entry(message).State == EntityState.Detached)
            {
                CaseMessages.Add(message);
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
            task.Property(t => t.QueueCode).HasMaxLength(80);
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

        ConfigureCases(modelBuilder);

        modelBuilder.Entity<CaseNote>(note =>
        {
            note.ToTable("case_note");
            note.HasKey(n => new { n.TenantId, n.Id });
            note.HasAuditColumns();
            note.Property(n => n.BusinessRefType).HasMaxLength(30);
        });
    }

    private static void ConfigureCases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceCase>(c =>
        {
            c.ToTable("service_case");
            c.HasKey(x => new { x.TenantId, x.Id });
            c.HasAuditColumns();
            c.Ignore(x => x.DomainEvents);
            c.Ignore(x => x.HasChanges);
            c.Ignore(x => x.NewHistory);
            c.Ignore(x => x.NewMessages);
            c.Ignore(x => x.IsOpen);
            c.Ignore(x => x.CurrentStep);
            c.Property(x => x.CaseNumber).HasMaxLength(25);
            c.Property(x => x.CaseTypeCode).HasMaxLength(30);
            c.Property(x => x.Family).HasMaxLength(12);
            c.Property(x => x.Status).HasConversion(new UpperCaseEnumConverter<CaseStatus>()).HasMaxLength(22);
            c.Property(x => x.Outcome).HasMaxLength(14);
            c.Property(x => x.ReceivedChannel).HasMaxLength(20);
            c.Property(x => x.ComplainantName).HasMaxLength(200);
            c.Property(x => x.ComplainantContact).HasMaxLength(200);
            c.Property(x => x.RaisedByAudience).HasMaxLength(10);
            c.Property(x => x.RaisedByIntermediaryCode).HasMaxLength(30);
            c.Property(x => x.RaisedByProviderId).HasMaxLength(40);
            c.Property(x => x.CategoryCode).HasMaxLength(30);
            c.Property(x => x.ProductCode).HasMaxLength(15);
            c.Property(x => x.OrgNodeCode).HasMaxLength(15);
            c.Property(x => x.OmbudSchemeCode).HasMaxLength(20);
            c.Property(x => x.OmbudReference).HasMaxLength(60);
            c.Property(x => x.OmbudOutcome).HasMaxLength(14);
            c.HasMany(x => x.Links).WithOne().HasForeignKey(l => new { l.TenantId, l.CaseId });
            c.Navigation(x => x.Links).UsePropertyAccessMode(PropertyAccessMode.Field);

            // The history, messages and redress rows belong to the case (FKs in V0060): EF writes the case first.
            c.HasMany<CaseHistoryEntry>().WithOne().HasForeignKey(h => new { h.TenantId, h.CaseId });
            c.HasMany<CaseMessage>().WithOne().HasForeignKey(m => new { m.TenantId, m.CaseId });
            c.HasMany<CaseRedress>().WithOne().HasForeignKey(r => new { r.TenantId, r.CaseId });
        });

        modelBuilder.Entity<CaseLink>(l =>
        {
            l.ToTable("case_link");
            l.HasKey(x => new { x.TenantId, x.Id });
            l.Property(x => x.LinkType).HasMaxLength(20);
            l.Property(x => x.Reference).HasMaxLength(60);
            l.Property(x => x.AddedBy).HasMaxLength(80);
        });

        modelBuilder.Entity<CaseHistoryEntry>(h =>
        {
            h.ToTable("case_event");
            h.HasKey(x => new { x.TenantId, x.Id });
            h.Property(x => x.EventType).HasMaxLength(30);
            h.Property(x => x.Actor).HasMaxLength(80);
            h.Property(x => x.Detail).HasMaxLength(2000);
        });

        modelBuilder.Entity<CaseMessage>(m =>
        {
            m.ToTable("case_message");
            m.HasKey(x => new { x.TenantId, x.Id });
            m.Property(x => x.Direction).HasMaxLength(3);
            m.Property(x => x.AuthorAudience).HasMaxLength(10);
        });

        modelBuilder.Entity<CaseRedress>(r =>
        {
            r.ToTable("case_redress");
            r.HasKey(x => new { x.TenantId, x.Id });
            r.Ignore(x => x.DomainEvents);
            r.Ignore(x => x.HasChanges);
            r.Ignore(x => x.NewHistory);
            r.Ignore(x => x.SubjectHash);
            r.Property(x => x.RedressType).HasMaxLength(14);
            r.Property(x => x.Amount).HasPrecision(18, 2);
            r.Property(x => x.Currency).HasColumnType("char(3)");
            r.Property(x => x.Status).HasConversion(new UpperCaseEnumConverter<RedressStatus>()).HasMaxLength(12);
        });
    }
}
