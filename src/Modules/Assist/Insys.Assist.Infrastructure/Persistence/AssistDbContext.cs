/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insys.Assist.Infrastructure.Persistence;

/// <summary>The Assist module's context: its own schema only, on its own database role (AIA-062, DBA-004).</summary>
public sealed class AssistDbContext(DbContextOptions<AssistDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "assist";

    public override string Schema => SchemaName;

    public DbSet<AssistTenantSetting> Settings => Set<AssistTenantSetting>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<AssistTenantSetting>(setting =>
        {
            setting.ToTable("tenant_setting");
            setting.HasKey(s => new { s.TenantId, s.Id });
            setting.HasAuditColumns();
            setting.Ignore(s => s.DomainEvents);
            setting.Ignore(s => s.HasChanges);
            setting.Ignore(s => s.ResidencyAllowsInference);
            setting.Property(s => s.Jurisdiction).HasColumnType("char(2)");
            setting.Property(s => s.InferenceRegion).HasMaxLength(20);
            setting.Property(s => s.LegalBasis).HasMaxLength(500);
        });
}

internal sealed class AssistRepository(AssistDbContext db) : Application.IAssistRepository
{
    public Task<AssistTenantSetting?> Setting(CancellationToken cancellationToken) => db.Settings.SingleOrDefaultAsync(cancellationToken);

    public void Add(AssistTenantSetting setting) => db.Settings.Add(setting);
}
