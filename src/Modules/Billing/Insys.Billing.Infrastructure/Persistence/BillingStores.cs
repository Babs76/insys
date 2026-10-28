/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
using Insys.Billing.Application;
using Insys.Billing.Contracts;
using Insys.Billing.Domain.Billing;
using Insys.Billing.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Billing.Infrastructure.Persistence;

/// <summary>
/// Billing's records, checked against the caller's scope for the endpoint's permission as they are loaded (F103,
/// addenda/access-scopes.md §1, §4; OQ-081): an invoice by its product and node; an account when any of its schedules is
/// in scope (its view then shows only that part); a refund only when all of them are. Outside it, a read finds nothing (404) and an action is refused (403 OUT_OF_SCOPE). Jobs and
/// consumers are unrestricted.
/// </summary>
internal sealed class BillingRepository(BillingDbContext db, IExecutionContext execution) : IBillingRepository
{
    public async Task<BillingAccount?> Account(Guid id, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.Include(a => a.Schedules).ThenInclude(s => s.Instalments).Include(a => a.Arrears).AsSplitQuery()
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return account is null || execution.CurrentScope.IsUnrestricted || account.Schedules.Any(s => execution.CurrentScope.Allows(s.ProductCode, s.OrgNodeCode))
            ? account
            : execution.InScope(account, null, null);
    }

    public async Task<Invoice?> Invoice(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(i => i.Lines).Include(i => i.Adjustments).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return execution.InScope(invoice, invoice?.ProductCode, invoice?.OrgNodeCode);
    }

    public async Task<IReadOnlyList<Invoice>> Invoices(Guid accountId, CancellationToken cancellationToken) =>
        await db.Invoices.Include(i => i.Lines).Include(i => i.Adjustments).Where(i => i.BillingAccountId == accountId).OrderBy(i => i.DueDate).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);

    public async Task<RefundRequest?> Refund(Guid id, CancellationToken cancellationToken)
    {
        var refund = await db.Refunds.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (refund is null || execution.CurrentScope.IsUnrestricted)
        {
            return refund;
        }

        // A refund is paid from the whole account's credit: only a caller covering every schedule on the account sees it (OQ-081).
        var schedules = db.Set<PremiumSchedule>().AsNoTracking().Where(s => s.BillingAccountId == refund.BillingAccountId);
        var whole = await schedules.CountAsync(cancellationToken)
            == await schedules.InScope(execution.CurrentScope, s => s.ProductCode, s => s.OrgNodeCode).CountAsync(cancellationToken);
        return whole ? refund : execution.InScope(refund, null, null);
    }

    public async Task<IReadOnlyList<BillingAccount>> Accounts(Guid? payerPartyId, Guid? policyRef, AccountStatus? status, Guid? afterId, int limit,
        CancellationToken cancellationToken)
    {
        var accounts = db.Accounts.AsNoTracking().Include(a => a.Schedules).ThenInclude(s => s.Instalments).Include(a => a.Arrears).AsSplitQuery();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            var inScope = db.Set<PremiumSchedule>().AsNoTracking().InScope(execution.CurrentScope, s => s.ProductCode, s => s.OrgNodeCode);
            accounts = accounts.Where(a => inScope.Any(s => s.BillingAccountId == a.Id));
        }

        if (payerPartyId is { } payer)
        {
            accounts = accounts.Where(a => a.PayerPartyId == payer);
        }

        if (policyRef is { } policy)
        {
            accounts = accounts.Where(a => a.Schedules.Any(s => s.PolicyRef == policy));
        }

        if (status is { } s)
        {
            accounts = accounts.Where(a => a.Status == s);
        }

        if (afterId is { } after)
        {
            accounts = accounts.Where(a => a.Id.CompareTo(after) > 0);
        }

        return await accounts.OrderBy(a => a.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RefundRequest>> Refunds(Guid? accountId, RefundStatus? status, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var refunds = db.Refunds.AsNoTracking();
        if (!execution.CurrentScope.IsUnrestricted)
        {
            // A refund is paid from the whole account's credit: only accounts all of whose schedules are in scope (OQ-081).
            var schedules = db.Set<PremiumSchedule>().AsNoTracking();
            var inScope = schedules.InScope(execution.CurrentScope, s => s.ProductCode, s => s.OrgNodeCode);
            refunds = refunds.Where(r => schedules.Count(s => s.BillingAccountId == r.BillingAccountId) == inScope.Count(s => s.BillingAccountId == r.BillingAccountId)
                                         && schedules.Any(s => s.BillingAccountId == r.BillingAccountId));
        }

        if (accountId is { } account)
        {
            refunds = refunds.Where(r => r.BillingAccountId == account);
        }

        if (status is { } s)
        {
            refunds = refunds.Where(r => r.Status == s);
        }

        if (afterId is { } after)
        {
            refunds = refunds.Where(r => r.Id.CompareTo(after) > 0);
        }

        return await refunds.OrderBy(r => r.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<BillingAccount?> AccountForPolicy(string sourceDomain, Guid policyRef, CancellationToken cancellationToken) =>
        await db.Set<PremiumSchedule>().AsNoTracking().Where(s => s.SourceDomain == sourceDomain && s.PolicyRef == policyRef).Select(s => (Guid?)s.BillingAccountId)
            .FirstOrDefaultAsync(cancellationToken) is { } id ? await Account(id, cancellationToken) : null;

    public async Task<BillingAccount?> OpenAccountOf(Guid payerPartyId, string currency, CancellationToken cancellationToken) =>
        await db.Set<BillingAccount>().AsNoTracking().Where(a => a.PayerPartyId == payerPartyId && a.Currency == currency && a.Status != AccountStatus.Closed)
            .OrderBy(a => a.Id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken) is { } id ? await Account(id, cancellationToken) : null;

    public async Task<IReadOnlyList<Guid>> AccountsWithDueInstalments(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Set<Instalment>().AsNoTracking().Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate <= day)
            .Join(db.Set<PremiumSchedule>(), i => i.ScheduleId, s => s.Id, (i, s) => s.BillingAccountId).Distinct().Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> AccountsToAge(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Invoices.AsNoTracking()
            .Where(i => i.DocumentType == DocumentType.Invoice && i.DueDate < day && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartPaid || i.Status == InvoiceStatus.Overdue))
            .Select(i => i.BillingAccountId)
            .Union(db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.InArrears).Select(a => a.Id))
            .Distinct().Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BillingAccount>> InArrears(CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking().Include(a => a.Schedules).Include(a => a.Arrears).AsSplitQuery()
            .Where(a => a.Arrears.Any(c => c.Status == "OPEN")).ToListAsync(cancellationToken);

    public void Add(BillingAccount account) => db.Accounts.Add(account);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Add(RefundRequest refund) => db.Refunds.Add(refund);
}

internal sealed class BillingCodes(BillingDbContext db) : IBillingCodes
{
    public async Task<BillingFrequency?> Frequency(string code, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<BillingFrequencies>(day).Where(f => f.Code == code).Select(f => new BillingFrequency(f.Code, f.PeriodsPerYear)).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> CollectionMethodExists(string code, DateOnly day, CancellationToken cancellationToken) =>
        InForce<CollectionMethods>(day).AnyAsync(m => m.MethodCode == code, cancellationToken);

    public Task<string?> PaymentMethodOf(string collectionMethod, DateOnly day, CancellationToken cancellationToken) =>
        InForce<CollectionMethods>(day).Where(m => m.MethodCode == collectionMethod).Select(m => (string?)m.PaymentMethod).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AgeingBucket>> AgeingBuckets(DateOnly day, CancellationToken cancellationToken) =>
        await InForce<ArrearsAgeingBuckets>(day).OrderBy(b => b.DaysFrom).Select(b => new AgeingBucket(b.BucketCode, b.DaysFrom, b.DaysTo, b.Action))
            .ToListAsync(cancellationToken);

    public async Task<AdjustmentReason?> AdjustmentReason(string code, DateOnly day, CancellationToken cancellationToken) =>
        await InForce<AdjustmentAndReversalReasonCodes>(day).Where(r => r.Code == code).Select(r => new AdjustmentReason(r.Code, r.RequiresApproval))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>TODO(spec): numbers use the scheme's prefix and an ID-based suffix; sequential numbering needs a counter (OQ-058).</summary>
    public async Task<string> NumberPrefix(string documentType, DateOnly day, CancellationToken cancellationToken) =>
        (await InForce<DocumentNumberingSchemes>(day).Where(s => s.DocumentType == documentType).Select(s => s.Prefix).FirstOrDefaultAsync(cancellationToken)
            ?? (documentType == "CREDIT_NOTE" ? "CRN" : "INV")) + "-";

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(BillingStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

/// <summary>
/// The allocation plan for a received amount (BIL-003), read on Billing's own connection: the invoice it was for
/// first, then overdue invoices oldest first, then current ones by due date. TODO(spec): the allocation rules'
/// priority categories (fees, arrears, current, advance), tolerance and overpayment handling are not applied beyond
/// that order; an amount left over is not allocated (OQ-058).
/// </summary>
internal sealed class AllocationPlanner([FromKeyedServices(BillingDbContext.SchemaName)] NpgsqlDataSource dataSource) : IAllocationPlanner
{
    public async Task<IReadOnlyList<PlannedAllocation>> Plan(Guid tenantId, Guid? billingAccountId, Guid? invoiceId, decimal amount, string currency, DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.id, i.due_date, i.total_amount - i.allocated_amount - coalesce((SELECT sum(a.amount) FROM billing.adjustment a WHERE a.tenant_id = i.tenant_id AND a.invoice_id = i.id), 0)
            FROM billing.invoice i
            WHERE i.tenant_id = @t AND i.currency = @currency AND i.document_type = 'INVOICE'
              AND i.billing_account_id = coalesce(@account, (SELECT x.billing_account_id FROM billing.invoice x WHERE x.tenant_id = @t AND x.id = @invoice))
              AND i.status IN ('ISSUED', 'PART_PAID', 'OVERDUE')
            ORDER BY (i.id = @invoice) DESC, (i.due_date < @day) DESC, i.due_date, i.id
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("account", NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = billingAccountId });
        command.Parameters.AddWithValue("currency", currency);
        command.Parameters.AddWithValue("invoice", invoiceId ?? Guid.Empty);
        command.Parameters.AddWithValue("day", day);
        var plan = new List<PlannedAllocation>();
        var remaining = amount;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (remaining > 0 && await reader.ReadAsync(cancellationToken))
            {
                var outstanding = reader.GetDecimal(2);
                if (outstanding > 0)
                {
                    var applied = Math.Min(outstanding, remaining);
                    plan.Add(new PlannedAllocation(reader.GetGuid(0), applied));
                    remaining -= applied;
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return plan;
    }
}

/// <summary>
/// A policy's invoiced instalments past their own due date whose invoice is not paid (issued, part paid or overdue). An
/// invoice can hold several instalments (a backdated policy), so instalments are counted, not invoices.
/// </summary>
internal sealed class PolicyArrearsQuery([FromKeyedServices(BillingDbContext.SchemaName)] NpgsqlDataSource dataSource) : IPolicyArrears
{
    public async Task<int?> PeriodsPerYear(Guid tenantId, string frequencyCode, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT periods_per_year FROM billing.ref_billing_frequencies
            WHERE (tenant_id = @t OR tenant_id IS NULL) AND code = @code AND record_status = ANY(@statuses) AND effective_from <= @day
              AND (effective_to IS NULL OR effective_to > @day)
            ORDER BY tenant_id NULLS LAST, version DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("code", frequencyCode);
        command.Parameters.AddWithValue("statuses", BuildingBlocks.Infrastructure.StaticData.StaticLifecycle.Reference.InForce);
        command.Parameters.AddWithValue("day", day);
        var periods = await command.ExecuteScalarAsync(cancellationToken) as int?;
        await transaction.CommitAsync(cancellationToken);
        return periods;
    }

    public async Task<PolicyArrears> For(Guid tenantId, string sourceDomain, Guid policyRef, DateOnly asOf, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TenantSession.SetTenant(connection, transaction, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*), min(i.due_date)
            FROM billing.premium_instalment i
            JOIN billing.premium_schedule s ON s.tenant_id = i.tenant_id AND s.id = i.schedule_id
            JOIN billing.invoice v ON v.tenant_id = i.tenant_id AND v.id = i.invoice_id
            WHERE i.tenant_id = @t AND s.source_domain = @domain AND s.policy_ref = @policy AND i.status = 'INVOICED'
              AND v.status IN ('ISSUED', 'PART_PAID', 'OVERDUE') AND i.due_date < @day
            """, connection, transaction);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("domain", sourceDomain);
        command.Parameters.AddWithValue("policy", policyRef);
        command.Parameters.AddWithValue("day", asOf);
        PolicyArrears arrears;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            arrears = new PolicyArrears((int)reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1));
        }

        await transaction.CommitAsync(cancellationToken);
        return arrears;
    }
}

internal sealed class BillingEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Billing.BillingAccountOpened e => new Contracts.BillingAccountOpened(e.AccountId, e.AccountNumber, e.PayerPartyId, e.Currency),
        Domain.Billing.InvoiceIssued e => new Contracts.InvoiceIssued(e.InvoiceId, e.InvoiceNumber, e.BillingAccountId, e.PayerPartyId, e.PaymentMethod, e.SourceDomain, e.PolicyRef, e.ProductVersionRef,
            e.IssueDate, e.DueDate, e.Currency, e.NetAmount, e.TaxAmount, e.TotalAmount),
        Domain.Billing.CreditNoteIssued e => new Contracts.CreditNoteIssued(e.CreditNoteId, e.CreditNoteNumber, e.InvoiceId, e.BillingAccountId, e.SourceDomain,
            e.PolicyRef, e.IssueDate, e.Currency, e.TotalAmount, e.ReasonCode),
        Domain.Billing.InvoiceSettled e => new Contracts.InvoiceSettled(e.InvoiceId, e.BillingAccountId, e.SourceDomain, e.PolicyRef),
        Domain.Billing.InvoiceOverdue e => new Contracts.InvoiceOverdue(e.InvoiceId, e.BillingAccountId, e.SourceDomain, e.PolicyRef, e.DueDate, e.Outstanding, e.Currency),
        Domain.Billing.ArrearsAgeingBlocked e => new Contracts.ArrearsAgeingBlocked(e.AccountId, e.BucketCode, e.DaysOverdue),
        Domain.Billing.ArrearsDetected e => new Contracts.ArrearsDetected(e.AccountId, e.BucketCode, e.DaysOverdue, e.AmountOverdue, e.Currency, e.AccountNumber,
            e.PayerPartyId, e.NextAction, e.ProductCode, e.OrgNodeCode),
        Domain.Billing.RefundApproved e => new Contracts.RefundApproved(e.RefundId, e.BillingAccountId, e.PayerPartyId, e.BankAccountId, e.Amount, e.Currency),
        RefundRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(BillingApprovals.RefundBusinessRefType, e.RefundId, BillingApprovals.RefundApprovalType,
            e.RequestedBy, e.Amount, e.Currency, $"Refund ({e.ReasonCode})", e.SubjectHash),
        _ => null,
    };
}
