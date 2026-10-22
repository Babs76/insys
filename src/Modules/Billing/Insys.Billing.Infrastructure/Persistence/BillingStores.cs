using Insys.Billing.Application;
using Insys.Billing.Contracts;
using Insys.Billing.Domain.Billing;
using Insys.Billing.Infrastructure.StaticData;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.Billing.Infrastructure.Persistence;

internal sealed class BillingRepository(BillingDbContext db) : IBillingRepository
{
    public Task<BillingAccount?> Account(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.Include(a => a.Schedules).ThenInclude(s => s.Instalments).Include(a => a.Arrears).AsSplitQuery()
            .SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public Task<Invoice?> Invoice(Guid id, CancellationToken cancellationToken) =>
        db.Invoices.Include(i => i.Lines).Include(i => i.Adjustments).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Invoice>> Invoices(Guid accountId, CancellationToken cancellationToken) =>
        await db.Invoices.Include(i => i.Lines).Include(i => i.Adjustments).Where(i => i.BillingAccountId == accountId).OrderBy(i => i.DueDate).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);

    public Task<RefundRequest?> Refund(Guid id, CancellationToken cancellationToken) => db.Refunds.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> AccountsWithDueInstalments(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Set<Instalment>().AsNoTracking().Where(i => i.Status == InstalmentStatus.Scheduled && i.DueDate <= day)
            .Join(db.Set<PremiumSchedule>(), i => i.ScheduleId, s => s.Id, (i, s) => s.BillingAccountId).Distinct().Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> AccountsToAge(DateOnly day, int limit, CancellationToken cancellationToken) =>
        await db.Invoices.AsNoTracking()
            .Where(i => i.DocumentType == DocumentType.Invoice && i.DueDate < day && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartPaid || i.Status == InvoiceStatus.Overdue))
            .Select(i => i.BillingAccountId)
            .Union(db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.InArrears).Select(a => a.Id))
            .Distinct().Take(limit).ToListAsync(cancellationToken);

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

internal sealed class BillingEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        Domain.Billing.BillingAccountOpened e => new Contracts.BillingAccountOpened(e.AccountId, e.AccountNumber, e.PayerPartyId, e.Currency),
        Domain.Billing.InvoiceIssued e => new Contracts.InvoiceIssued(e.InvoiceId, e.InvoiceNumber, e.BillingAccountId, e.PayerPartyId, e.PaymentMethod, e.SourceDomain, e.PolicyRef, e.ProductVersionRef,
            e.IssueDate, e.DueDate, e.Currency, e.NetAmount, e.TaxAmount, e.TotalAmount),
        Domain.Billing.CreditNoteIssued e => new Contracts.CreditNoteIssued(e.CreditNoteId, e.CreditNoteNumber, e.InvoiceId, e.BillingAccountId, e.SourceDomain,
            e.PolicyRef, e.IssueDate, e.Currency, e.TotalAmount, e.ReasonCode),
        Domain.Billing.InvoiceSettled e => new Contracts.InvoiceSettled(e.InvoiceId, e.BillingAccountId),
        Domain.Billing.ArrearsDetected e => new Contracts.ArrearsDetected(e.AccountId, e.BucketCode, e.DaysOverdue, e.AmountOverdue, e.Currency),
        Domain.Billing.RefundApproved e => new Contracts.RefundApproved(e.RefundId, e.BillingAccountId, e.Amount, e.Currency),
        RefundRequested e => new BuildingBlocks.Contracts.Approvals.ApprovalRequested(BillingApprovals.RefundBusinessRefType, e.RefundId, BillingApprovals.RefundApprovalType,
            e.RequestedBy, e.Amount, e.Currency, $"Refund ({e.ReasonCode})"),
        _ => null,
    };
}
