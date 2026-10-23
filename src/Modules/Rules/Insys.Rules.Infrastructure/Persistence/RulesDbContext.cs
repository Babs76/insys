/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rules
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
using Insys.Rules.Domain.Decisions;
using Insys.Rules.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Rules.Infrastructure.Persistence;

/// <summary>The Rules module's context: its own schema only, on its own database role (DBA-004).</summary>
public sealed class RulesDbContext(DbContextOptions<RulesDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "rules";

    public override string Schema => SchemaName;

    public DbSet<RuleDecision> Decisions => Set<RuleDecision>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        RulesStaticData.Configure(modelBuilder);

        // decision_log is partitioned by evaluated_at, which is part of its key; rows are never updated.
        modelBuilder.Entity<RuleDecision>(decision =>
        {
            decision.ToTable("decision_log");
            decision.HasKey(d => new { d.TenantId, d.Id, d.EvaluatedAt });
            decision.HasAuditColumns(updatable: false);
            decision.Ignore(d => d.DomainEvents);
            decision.Ignore(d => d.HasChanges);
            decision.Ignore(d => d.RowVersion);
            decision.Property(d => d.RulesetCode).HasColumnName("ruleset_code").HasMaxLength(20);
            decision.Property(d => d.RulesetVersion).HasColumnName("ruleset_version");
            decision.Property(d => d.SubjectType).HasMaxLength(30);
            decision.Property(d => d.Inputs).HasColumnType("jsonb");
            decision.Property(d => d.Outcome).HasMaxLength(15);
        });
    }
}
