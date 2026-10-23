/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
using System.Text.Json;
using Insys.Audit.Infrastructure.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.Audit.Infrastructure.Persistence;

/// <summary>A row in audit.audit_event: insert-only and partitioned by month (DBA-016).</summary>
public sealed class AuditEvent
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "";

    public string EventType { get; set; } = "";

    public string ActorType { get; set; } = "";

    public string ActorId { get; set; } = "";

    public string Operation { get; set; } = "";

    public string TargetType { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Outcome { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }

    public Guid CorrelationId { get; set; }

    public JsonElement? Details { get; set; }
}

/// <summary>The Audit module's context: the audit schema only, on the insys_audit role (insert and select only).</summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ModuleDbContextDependencies dependencies) : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        AuditStaticData.Configure(modelBuilder);
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_event");
            entity.HasKey(e => new { e.TenantId, e.Id, e.OccurredAt });
            entity.Property(e => e.CreatedBy).HasMaxLength(80);
            entity.Property(e => e.EventType).HasMaxLength(40);
            entity.Property(e => e.ActorType).HasMaxLength(10);
            entity.Property(e => e.ActorId).HasMaxLength(80);
            entity.Property(e => e.Operation).HasMaxLength(40);
            entity.Property(e => e.TargetType).HasMaxLength(30);
            entity.Property(e => e.TargetId).HasMaxLength(80);
            entity.Property(e => e.Outcome).HasMaxLength(10);
            entity.Property(e => e.Details).HasColumnType("jsonb");
        });
    }
}
