/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Distribution
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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Distribution.Application;
using Insys.Distribution.Contracts;
using Insys.Distribution.Domain;
using Insys.Distribution.Domain.Commission;
using Insys.Distribution.Domain.Intermediaries;
using Insys.Distribution.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Distribution.Infrastructure.Persistence;

internal sealed class DistributionRepository(DistributionDbContext db) : IDistributionRepository
{
    public void Request(Communications.Contracts.NotificationRequested notice, DateTimeOffset now) => db.Publish(notice, "GeneralPolicy", notice.SourceId, now);

    public Task<CommissionEntitlement?> Entitlement(Guid id, CancellationToken cancellationToken) =>
        Entitlements().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<CommissionEntitlement?> EntitlementFor(string sourceType, Guid sourceId, string intermediaryCode, CancellationToken cancellationToken) =>
        Entitlements().SingleOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId && e.IntermediaryCode == intermediaryCode, cancellationToken);

    public async Task<IReadOnlyList<CommissionEntitlement>> EntitlementsForSource(string sourceType, Guid sourceId, CancellationToken cancellationToken) =>
        await Entitlements().Where(e => e.SourceType == sourceType && e.SourceId == sourceId).OrderBy(e => e.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CommissionEntitlement>> EntitlementsForPolicy(string sourceDomain, Guid policyRef, CancellationToken cancellationToken) =>
        await Entitlements().Where(e => e.SourceDomain == sourceDomain && e.PolicyRef == policyRef).OrderBy(e => e.Id).ToListAsync(cancellationToken);

    public Task<bool> HasEntitlement(string sourceDomain, Guid policyRef, string intermediaryCode, CancellationToken cancellationToken) =>
        db.Entitlements.AnyAsync(e => e.SourceDomain == sourceDomain && e.PolicyRef == policyRef && e.IntermediaryCode == intermediaryCode, cancellationToken);

    public async Task<(IReadOnlyList<CommissionEntitlement> Payable, IReadOnlyList<CommissionEntitlement> ToRecover)> Due(string intermediaryCode, string currency,
        CancellationToken cancellationToken)
    {
        var payable = await Entitlements()
            .Where(e => e.IntermediaryCode == intermediaryCode && e.Currency == currency && e.Status == EntitlementStatus.Payable && e.StatementId == null)
            .OrderBy(e => e.Id).ToListAsync(cancellationToken);
        var toRecover = await Entitlements()
            .Where(e => e.IntermediaryCode == intermediaryCode && e.Currency == currency && e.Status == EntitlementStatus.ClawedBack && e.StatementId != null
                        && e.Clawbacks.Any(c => c.StatementId == null))
            .OrderBy(e => e.Id).ToListAsync(cancellationToken);
        return (payable, toRecover);
    }

    public async Task<IReadOnlyList<CommissionEntitlement>> OnStatement(Guid statementId, CancellationToken cancellationToken) =>
        await Entitlements().Where(e => e.StatementId == statementId || e.Clawbacks.Any(c => c.StatementId == statementId)).OrderBy(e => e.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CommissionStatement>> Statements(string? intermediaryCode, string? periodCode, StatementStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var query = db.Statements.AsNoTracking();
        if (intermediaryCode is not null)
        {
            query = query.Where(s => s.IntermediaryCode == intermediaryCode);
        }

        if (periodCode is not null)
        {
            query = query.Where(s => s.PeriodCode == periodCode);
        }

        if (status is { } st)
        {
            query = query.Where(s => s.Status == st);
        }

        if (afterId is { } after)
        {
            query = query.Where(s => s.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(s => s.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CommissionEntitlement>> List(string? intermediaryCode, Guid? policyRef, EntitlementStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken, Guid? statementId = null)
    {
        var query = Entitlements().AsNoTracking();
        if (statementId is { } statement)
        {
            query = query.Where(e => e.StatementId == statement || e.Clawbacks.Any(c => c.StatementId == statement));
        }

        if (intermediaryCode is not null)
        {
            query = query.Where(e => e.IntermediaryCode == intermediaryCode);
        }

        if (policyRef is { } policy)
        {
            query = query.Where(e => e.PolicyRef == policy);
        }

        if (status is { } s)
        {
            query = query.Where(e => e.Status == s);
        }

        if (afterId is { } after)
        {
            query = query.Where(e => e.Id.CompareTo(after) > 0);
        }

        return await query.OrderBy(e => e.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<CommissionStatement?> Statement(Guid id, CancellationToken cancellationToken) => db.Statements.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<CommissionStatement?> Statement(string intermediaryCode, string periodCode, CancellationToken cancellationToken) =>
        db.Statements.SingleOrDefaultAsync(s => s.IntermediaryCode == intermediaryCode && s.PeriodCode == periodCode, cancellationToken);

    public void Add(CommissionEntitlement entitlement) => db.Entitlements.Add(entitlement);

    public void Add(CommissionStatement statement) => db.Statements.Add(statement);

    private IQueryable<CommissionEntitlement> Entitlements() => db.Entitlements.Include(e => e.Clawbacks);
}

internal sealed class DistributionCodes(DistributionDbContext db) : IDistributionCodes
{
    public async Task<IntermediaryInfo?> Intermediary(string intermediaryCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<IntermediaryRegister>(day).Where(i => i.IntermediaryCode == intermediaryCode).OrderByDescending(i => i.Version)
            .Select(i => new IntermediaryInfo(i.IntermediaryCode, i.Status, i.PartyId, i.ContactPartyId)).FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> PlanCodeFor(string intermediaryCode, string productCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<IntermediaryProductPermissions>(day).Where(p => p.Intermediary == intermediaryCode && p.Product == productCode && p.PlanCode != null)
            .OrderBy(p => p.Code).Select(p => p.PlanCode).FirstOrDefaultAsync(cancellationToken);

    public async Task<CommissionPlan?> Plan(string planCode, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<CommissionPlans>(day).Where(p => p.PlanCode == planCode).OrderByDescending(p => p.Version)
            .Select(p => new CommissionPlan(p.PlanCode, p.Version, p.Basis, p.Rate, p.ClawbackRule ?? ClawbackRules.Unearned)).FirstOrDefaultAsync(cancellationToken);

    public async Task<CommissionPlan?> PlanVersion(string planCode, int version, CancellationToken cancellationToken) =>
        await db.Set<CommissionPlans>().AsNoTracking().Where(p => p.PlanCode == planCode && p.Version == version)
            .Select(p => new CommissionPlan(p.PlanCode, p.Version, p.Basis, p.Rate, p.ClawbackRule ?? ClawbackRules.Unearned)).FirstOrDefaultAsync(cancellationToken);

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(DistributionStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>DST-001 for the policy domains, on Distribution's own connection: the register's status and a permission in force.</summary>
internal sealed class Intermediaries([FromKeyedServices(DistributionDbContext.SchemaName)] NpgsqlDataSource dataSource) : IIntermediaries
{
    public async Task<bool> IsActiveAndLicensed(Guid tenantId, string intermediaryCode, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status = 'ACTIVE' AND jsonb_typeof(licence_numbers) IN ('array', 'object') AND licence_numbers NOT IN ('[]'::jsonb, '{}'::jsonb)
            FROM commission.mst_intermediary_register
            WHERE tenant_id = @t AND intermediary_code = @i AND record_status = ANY(@reference) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("i", intermediaryCode);
        command.Parameters.AddWithValue("reference", StaticLifecycle.Reference.InForce);
        command.Parameters.AddWithValue("day", day);
        var standing = await command.ExecuteScalarAsync(cancellationToken) is true;
        await transaction.CommitAsync(cancellationToken);
        return standing;
    }

    public async Task<IReadOnlySet<string>> WithSubBrokers(Guid tenantId, string intermediaryCode, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE register AS (
              SELECT DISTINCT ON (intermediary_code) intermediary_code, hierarchy_node::text AS hierarchy_node FROM commission.mst_intermediary_register
              WHERE tenant_id = @t AND record_status = ANY(@reference) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
              ORDER BY intermediary_code, version DESC),
            hierarchy AS (
              SELECT DISTINCT ON (node_code) node_code::text AS node_code, parent::text AS parent FROM commission.cfg_distribution_hierarchy
              WHERE tenant_id = @t AND record_status = ANY(@config) AND effective_from <= @day AND (effective_to IS NULL OR effective_to > @day)
              ORDER BY node_code, version DESC),
            below (node_code) AS (
              SELECT hierarchy_node FROM register WHERE intermediary_code = @i
              UNION SELECT h.node_code FROM hierarchy h JOIN below b ON h.parent = b.node_code)
            SELECT intermediary_code FROM register
            WHERE intermediary_code = @i OR hierarchy_node IN (SELECT node_code FROM below WHERE node_code <> (SELECT hierarchy_node FROM register WHERE intermediary_code = @i))
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("i", intermediaryCode);
        command.Parameters.AddWithValue("reference", StaticLifecycle.Reference.InForce);
        command.Parameters.AddWithValue("config", StaticLifecycle.Configuration.InForce);
        command.Parameters.AddWithValue("day", day);
        var codes = new HashSet<string>(StringComparer.Ordinal) { intermediaryCode };
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                codes.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return codes;
    }

    public async Task<IntermediaryRefusal?> CanSell(Guid tenantId, string intermediaryCode, string productCode, string transaction, DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transactionScope = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transactionScope, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT status FROM commission.mst_intermediary_register
                    WHERE tenant_id = @t AND intermediary_code = @i AND record_status = ANY(@reference) AND effective_from <= @day
                      AND (effective_to IS NULL OR effective_to > @day)
                    ORDER BY version DESC LIMIT 1),
                   EXISTS (SELECT 1 FROM commission.cfg_intermediary_product_permissions
                           WHERE tenant_id = @t AND intermediary = @i AND product = @p AND record_status = ANY(@config) AND effective_from <= @day
                             AND (effective_to IS NULL OR effective_to > @day)
                             AND (cardinality(transactions_allowed) = 0 OR @x = ANY(transactions_allowed)))
            """, connection, transactionScope);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("i", intermediaryCode);
        command.Parameters.AddWithValue("p", productCode);
        command.Parameters.AddWithValue("x", transaction);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("reference", StaticLifecycle.Reference.InForce);
        command.Parameters.AddWithValue("config", StaticLifecycle.Configuration.InForce);
        string? status;
        bool permitted;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            status = reader.IsDBNull(0) ? null : reader.GetString(0);
            permitted = reader.GetBoolean(1);
        }

        await transactionScope.CommitAsync(cancellationToken);
        return status switch
        {
            null => new IntermediaryRefusal(DistributionErrorCodes.IntermediaryNotPermitted, $"{intermediaryCode} is not an intermediary in the register."),
            "SUSPENDED" or "TERMINATED" => new IntermediaryRefusal(DistributionErrorCodes.IntermediarySuspended,
                $"Intermediary {intermediaryCode} is {status} and starts no new business."),
            _ when !permitted => new IntermediaryRefusal(DistributionErrorCodes.IntermediaryNotPermitted,
                $"Intermediary {intermediaryCode} holds no permission in force for {productCode} ({transaction}) (DST-001)."),
            _ => null,
        };
    }
}

/// <summary>An intermediary's new register version follows its lifecycle (OQ-062 item 9).</summary>
internal sealed class IntermediaryLifecycleGuard : IStaticChangeGuard
{
    public string Schema => DistributionDbContext.SchemaName;

    public string Table => "mst_intermediary_register";

    public Result Check(StaticRecord proposed, StaticRecord? current) =>
        Intermediary.CheckChange((current as IntermediaryRegister)?.Status, ((IntermediaryRegister)proposed).Status);
}

/// <summary>An approved register version that suspends an intermediary raises IntermediarySuspended.</summary>
public sealed class IntermediaryStatusPolicy(DistributionDbContext db, IClock clock) : IIntegrationEventHandler<StaticDataChangeApproved>
{
    public async Task Handle(StaticDataChangeApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (approved.Record.Schema != DistributionDbContext.SchemaName || approved.Record.Table != "mst_intermediary_register"
            || await db.Set<IntermediaryRegister>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == approved.Record.RecordId, cancellationToken) is not { Status: "SUSPENDED" } row)
        {
            return;
        }

        db.Publish(new IntermediarySuspended(row.IntermediaryCode, row.EffectiveFrom), "Intermediary", row.Id, clock.UtcNow);
    }
}

internal sealed class DistributionEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Commission.CommissionAccrued e => new Contracts.CommissionAccrued(e.EntitlementId, e.IntermediaryCode, e.SourceDomain, e.PolicyRef, e.InvoiceId, e.PlanCode,
            e.PlanVersion, e.Amount, e.Currency, e.AccruedOn),
        Domain.Commission.CommissionClawedBack e => new Contracts.CommissionClawedBack(e.ClawbackId, e.EntitlementId, e.IntermediaryCode, e.SourceDomain, e.PolicyRef,
            e.Amount, e.Currency, e.Reason, e.ClawedBackOn),
        // B-001: the cut part of an outgoing broker's commission, reversed by Finance like any clawback.
        Domain.Commission.CommissionShared e => new Contracts.CommissionClawedBack(e.AdjustmentId, e.EntitlementId, e.IntermediaryCode, e.SourceDomain, e.PolicyRef,
            e.AmountBefore - e.Amount, e.Currency, e.Reason, e.SharedOn),
        Domain.Commission.CommissionStatementApproved e => new Contracts.CommissionStatementApproved(e.StatementId, e.IntermediaryCode, e.PeriodCode, e.PayeePartyId,
            e.BankAccountId, e.NetAmount, e.Currency),
        StatementSubmitted e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(DistributionApprovals.StatementBusinessRefType, e.StatementId,
            DistributionApprovals.StatementApprovalType, e.RequestedBy, e.NetAmount, e.Currency, $"Commission statement {e.IntermediaryCode} {e.PeriodCode}", e.SubjectHash),
        _ => null,
    };
}

internal sealed class DistributionPortalQueries(DistributionDbContext db) : IDistributionPortalQueries
{
    public async Task<IReadOnlyList<CommissionStatement>> Statements(IReadOnlyCollection<string> intermediaryCodes, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var codes = intermediaryCodes.ToList();
        var rows = db.Statements.AsNoTracking().Where(s => codes.Contains(s.IntermediaryCode) && s.Status != StatementStatus.Draft);
        if (afterId is { } after)
        {
            rows = rows.Where(s => s.Id.CompareTo(after) > 0);
        }

        return await rows.OrderBy(s => s.Id).Take(limit).ToListAsync(cancellationToken);
    }
}
