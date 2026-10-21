using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Tenancy.Application.Tenants;
using Insys.Tenancy.Contracts;
using Insys.Tenancy.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Tenancy.Infrastructure.Persistence;

internal sealed class TenantRepository(TenancyDbContext db) : ITenantRepository
{
    public Task<Tenant?> Get(Guid tenantId, CancellationToken cancellationToken) =>
        db.Tenants.Include("_onboardingCases").Include(t => t.StatusChanges).SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    public Task<bool> CodeExists(string tenantCode, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(t => t.TenantCode == tenantCode, cancellationToken);

    public void Add(Tenant tenant) => db.Tenants.Add(tenant);
}

internal sealed class TenantQueries(TenancyDbContext db) : ITenantQueries
{
    public async Task<IReadOnlyList<TenantSummary>> List(string? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Tenants.AsNoTracking();
        if (status is not null && Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(t => t.Status == parsed);
        }

        if (afterId is not null)
        {
            query = query.Where(t => t.Id.CompareTo(afterId.Value) > 0);
        }

        var rows = await query.OrderBy(t => t.Id).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(t => new TenantSummary(t.Id, t.TenantCode, t.LegalName, t.IsolationProfile.ToString(), t.Status.ToString(), t.RowVersion)).ToList();
    }

    public async Task<TenantDetail?> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().Include("_onboardingCases").Include(t => t.StatusChanges)
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        return tenant is null
            ? null
            : new TenantDetail(tenant.Id, tenant.TenantCode, tenant.LegalName, tenant.IsolationProfile.ToString(), tenant.Status.ToString(),
                tenant.Onboarding?.CurrentStep, tenant.Onboarding?.Status, OnboardingSteps.All,
                tenant.StatusChanges.OrderBy(c => c.ChangedAt)
                    .Select(c => new StatusChangeView(c.FromStatus.ToString(), c.ToStatus.ToString(), c.Reason, c.ChangedBy, c.ChangedAt)).ToList(),
                tenant.RowVersion);
    }
}

internal sealed class TenancyEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Tenants.TenantSuspended suspended => new Contracts.TenantSuspended(suspended.TenantId),
        _ => null,
    };
}

/// <summary>The tenant register for per-tenant background work such as the outbox relay. All tenants, including terminated ones, so pending events still drain.</summary>
internal sealed class TenantDirectory([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> GetActiveTenantIds(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM tenant.tenant ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

internal sealed class TenantStatusQuery([FromKeyedServices(TenancyDbContext.SchemaName)] NpgsqlDataSource dataSource) : ITenantStatusQuery
{
    public async Task<bool> IsActive(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT status FROM tenant.tenant WHERE id = @id");
        command.Parameters.AddWithValue("id", tenantId);
        return await command.ExecuteScalarAsync(cancellationToken) is string status && status == UpperCaseEnumConverter<TenantStatus>.ToCode(TenantStatus.Active);
    }
}
