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
