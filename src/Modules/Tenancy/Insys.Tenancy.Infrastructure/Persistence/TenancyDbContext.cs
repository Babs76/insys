/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Tenancy
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
using Insys.Tenancy.Domain.Tenants;
using Insys.Tenancy.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;

namespace Insys.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The Tenancy module's context: the tenant schema only. Platform operators act on behalf of a tenant here
/// (TEN-007), so this is the one context that allows <see cref="ModuleDbContext.ActAsTenant"/>.
/// </summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ModuleDbContextDependencies dependencies)
    : ModuleDbContext(options, dependencies)
{
    public const string SchemaName = "tenant";

    public override string Schema => SchemaName;

    protected override bool AllowsOperatorActingAsTenant => true;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        TenancyStaticData.Configure(modelBuilder);

        // tenant.tenant is the tenant register: no tenant_id column and no row-level security. Only this module's
        // role can read it; other modules ask through ITenantStatusQuery and ITenantDirectory.
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenant");
            tenant.HasKey(t => t.Id);
            tenant.Ignore(t => t.TenantId);
            tenant.Ignore(t => t.DomainEvents);
            tenant.Ignore(t => t.HasChanges);
            tenant.Ignore(t => t.Onboarding);
            tenant.Property(t => t.TenantCode).HasMaxLength(10);
            tenant.Property(t => t.LegalName).HasMaxLength(150);
            tenant.Property(t => t.TimeZone).HasMaxLength(50);
            tenant.Property(t => t.IsolationProfile).HasConversion<string>().HasColumnType("char(1)");
            tenant.Property(t => t.Status).HasConversion(new UpperCaseEnumConverter<TenantStatus>()).HasMaxLength(12);
            tenant.Property<DateTimeOffset>(UpdatedAt);
            tenant.Property<string>(UpdatedBy).HasMaxLength(80);
            tenant.HasMany<OnboardingCase>("_onboardingCases").WithOne().HasForeignKey(c => c.TenantId);
            tenant.HasMany(t => t.StatusChanges).WithOne().HasForeignKey(c => c.TenantId);
            tenant.Navigation(t => t.StatusChanges).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OnboardingCase>(onboarding =>
        {
            onboarding.ToTable("onboarding_case");
            onboarding.HasKey(c => new { c.TenantId, c.Id });
            onboarding.Property(c => c.CurrentStep).HasMaxLength(30);
            onboarding.Property(c => c.Status).HasMaxLength(12);
            onboarding.Property(c => c.Attestations).HasColumnName("attestations").HasColumnType("jsonb")
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, System.Text.Json.JsonSerializerOptions.Web),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<OnboardingAttestation>>(v, System.Text.Json.JsonSerializerOptions.Web) ?? new List<OnboardingAttestation>(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<IReadOnlyList<OnboardingAttestation>>(
                        (a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())), v => v.ToList()));
            onboarding.HasAuditColumns();
        });

        modelBuilder.Entity<StatusChange>(change =>
        {
            change.ToTable("tenant_status_history");
            change.HasKey(c => new { c.TenantId, c.Id });
            change.Property(c => c.FromStatus).HasConversion(new UpperCaseEnumConverter<TenantStatus>()).HasMaxLength(12);
            change.Property(c => c.ToStatus).HasConversion(new UpperCaseEnumConverter<TenantStatus>()).HasMaxLength(12);
            change.HasAuditColumns(updatable: false);
        });
    }
}
