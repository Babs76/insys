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
using FluentValidation;
using Insys.Billing.Contracts;
using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.Billing.Application;

public interface IBillingRepository
{
    Task<BillingAccount?> Account(Guid id, CancellationToken cancellationToken);

    Task<Invoice?> Invoice(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Invoice>> Invoices(Guid accountId, CancellationToken cancellationToken);

    Task<RefundRequest?> Refund(Guid id, CancellationToken cancellationToken);

    /// <summary>The account billing a domain's policy, through its premium schedule.</summary>
    Task<BillingAccount?> AccountForPolicy(string sourceDomain, Guid policyRef, CancellationToken cancellationToken);

    /// <summary>A payer's open account in a currency, the oldest first (a scheme sponsor's account for employer contributions).</summary>
    Task<BillingAccount?> OpenAccountOf(Guid payerPartyId, string currency, CancellationToken cancellationToken);

    /// <summary>Accounts with instalments due on or before the day that are not invoiced yet.</summary>
    Task<IReadOnlyList<Guid>> AccountsWithDueInstalments(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Accounts with an issued, part-paid or overdue invoice due before the day, or in arrears.</summary>
    Task<IReadOnlyList<Guid>> AccountsToAge(DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Accounts with an open arrears case, with their schedules and arrears (for the dashboard, GAP-046).</summary>
    Task<IReadOnlyList<BillingAccount>> InArrears(CancellationToken cancellationToken);

    void Add(BillingAccount account);

    void Add(Invoice invoice);

    void Add(RefundRequest refund);
}

/// <summary>The Billing module's static data in force.</summary>
public interface IBillingCodes
{
    Task<BillingFrequency?> Frequency(string code, DateOnly day, CancellationToken cancellationToken);

    Task<bool> CollectionMethodExists(string code, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The payment method of a collection method (billing.ref_collection_methods), for Payments to collect with.</summary>
    Task<string?> PaymentMethodOf(string collectionMethod, DateOnly day, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgeingBucket>> AgeingBuckets(DateOnly day, CancellationToken cancellationToken);

    Task<AdjustmentReason?> AdjustmentReason(string code, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The numbering prefix for a document type (billing.cfg_document_numbering_schemes), or a default.</summary>
    Task<string> NumberPrefix(string documentType, DateOnly day, CancellationToken cancellationToken);
}

public sealed record InstalmentView(Guid Id, DateOnly DueDate, decimal Amount, InstalmentStatus Status, Guid? InvoiceId);

public sealed record ScheduleView(Guid Id, string SourceDomain, Guid PolicyRef, string ProductVersionRef, string Frequency, DateOnly StartDate, DateOnly? EndDate,
    decimal InstalmentAmount, string Currency, IReadOnlyList<InstalmentView> Instalments);

public sealed record ArrearsView(string BucketCode, int DaysOverdue, decimal AmountOverdue, string NextAction, [property: OneOf("OPEN", "CLEARED")] string Status);

/// <param name="PartlyInScope">
/// True when the account also bills policies outside the caller's scope (F103, OQ-081): only the schedules in scope are
/// shown, and the account-level arrears are left out, so no other branch's or product's figures show through it.
/// </param>
public sealed record AccountView(Guid Id, string AccountNumber, Guid PayerPartyId, string Currency, string CollectionMethod, AccountStatus Status,
    IReadOnlyList<ScheduleView> Schedules, IReadOnlyList<ArrearsView> Arrears, long RowVersion, bool PartlyInScope)
{
    public static AccountView From(BillingAccount a, AccessScope scope)
    {
        var whole = AccountScopes.Whole(a, scope);
        return new(a.Id, a.AccountNumber, a.PayerPartyId, a.Currency, a.CollectionMethod, a.Status,
        a.Schedules.Where(s => whole || scope.Allows(s.ProductCode, s.OrgNodeCode)).Select(s => new ScheduleView(s.Id, s.SourceDomain, s.PolicyRef, s.ProductVersionRef, s.Frequency, s.StartDate, s.EndDate, s.InstalmentAmount,
            s.Currency, s.Instalments.OrderBy(i => i.DueDate).Select(i => new InstalmentView(i.Id, i.DueDate, i.Amount, i.Status, i.InvoiceId)).ToList())).ToList(),
        whole ? a.Arrears.Select(r => new ArrearsView(r.BucketCode, r.DaysOverdue, r.AmountOverdue, r.NextAction, r.Status)).ToList() : [], a.RowVersion, !whole);
    }
}

/// <summary>
/// An account shared by policies of several products or branches (F103, OQ-081): a caller covering only part of it sees
/// only that part, and may not run what works on the whole account (ageing, closing, issuing, refunds, new schedules).
/// </summary>
/// <summary>
/// The Billing dashboard figure OPEN_ARREARS (GAP-046): the open arrears cases and their amount overdue per currency, of
/// the accounts wholly in the caller's scope. A partly-scoped account is left out, so no other branch's or product's
/// figures show through it (OQ-081).
/// </summary>
public sealed record BillingSummaryQuery : IQuery<DashboardSummary>;

internal sealed class BillingSummaryHandler(IBillingRepository billing, IExecutionContext execution, IClock clock) : IQueryHandler<BillingSummaryQuery, DashboardSummary>
{
    public const string OpenArrears = "OPEN_ARREARS";

    public async Task<Result<DashboardSummary>> Handle(BillingSummaryQuery query, CancellationToken cancellationToken)
    {
        var open = (await billing.InArrears(cancellationToken)).Where(a => AccountScopes.Whole(a, execution.CurrentScope))
            .SelectMany(a => a.Arrears.Where(c => c.Status == "OPEN")).ToList();
        var amounts = open.GroupBy(c => c.Currency).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new MoneyAmount(g.Key, g.Sum(c => c.AmountOverdue))).ToList();
        return new DashboardSummary(clock.UtcNow, [new DashboardFigure(OpenArrears, Count: open.Count, Amounts: amounts)]);
    }
}

internal static class AccountScopes
{
    public static bool Whole(BillingAccount account, AccessScope scope) =>
        scope.IsUnrestricted || account.Schedules.All(s => scope.Allows(s.ProductCode, s.OrgNodeCode));

    public static Result RequireWhole(BillingAccount account, IExecutionContext execution) =>
        Whole(account, execution.CurrentScope)
            ? Result.Success()
            : Error.Forbidden(ErrorCodes.OutOfScope, "The account also bills policies outside your scope, so this account-wide operation is refused.");
}

public sealed record InvoiceLineView(int LineNo, string ChargeType, string Description, decimal Amount);

public sealed record InvoiceView(Guid Id, string InvoiceNumber, string DocumentType, Guid BillingAccountId, string SourceDomain, Guid PolicyRef, string ProductVersionRef,
    DateOnly IssueDate, DateOnly DueDate, string Currency, decimal NetAmount, decimal TaxAmount, decimal TotalAmount, decimal AllocatedAmount, decimal Credited,
    decimal Outstanding, InvoiceStatus Status, Guid? ReversesInvoiceId, IReadOnlyList<InvoiceLineView> Lines)
{
    public static InvoiceView From(Invoice i) => new(i.Id, i.InvoiceNumber, i.DocumentType.ToString(), i.BillingAccountId, i.SourceDomain, i.PolicyRef,
        i.ProductVersionRef, i.IssueDate, i.DueDate, i.Currency, i.NetAmount, i.TaxAmount, i.TotalAmount, i.AllocatedAmount, i.Credited, i.Outstanding,
        i.Status, i.ReversesInvoiceId, i.Lines.OrderBy(l => l.LineNo).Select(l => new InvoiceLineView(l.LineNo, l.ChargeType, l.Description, l.Amount)).ToList());
}

public sealed record RefundView(Guid Id, Guid BillingAccountId, decimal Amount, string Currency, string ReasonCode, RefundStatus Status, Guid? PaymentId, Guid? RequestedBy,
    Guid? DecidedBy, Guid? BankAccountId);

public sealed record OpenAccount(Guid PayerPartyId, string Currency, string CollectionMethod, string? MandateRef) : ICommand<AccountView>;

/// <summary>Command CreateSchedule (BIL-001): from an authorised policy transaction.</summary>
public sealed record CreateSchedule(Guid AccountId, string SourceDomain, Guid PolicyRef, string ProductVersionRef, string Frequency, DateOnly StartDate,
    DateOnly? EndDate, decimal InstalmentAmount) : ICommand<AccountView>;

public sealed record AmendSchedule(Guid AccountId, Guid ScheduleId, decimal InstalmentAmount, DateOnly EffectiveFrom) : ICommand<AccountView>;

/// <summary>Command IssueInvoice for every schedule of the account with instalments due by the day.</summary>
public sealed record IssueDueInvoices(Guid AccountId, DateOnly AsOf) : ICommand<IReadOnlyList<InvoiceView>>;

public sealed record RaiseCreditNote(Guid InvoiceId, decimal Amount, string ReasonCode) : ICommand<InvoiceView>;

/// <summary>Commands DetectArrears (with the overdue invoices) and the "Due date passed" transition of its invoices.</summary>
public sealed record AgeAccount(Guid AccountId, DateOnly Today) : ICommand<AccountView>;

public sealed record CloseAccount(Guid AccountId) : ICommand<AccountView>;

/// <param name="BankAccountId">The payer's verified bank account (party.bank_account) the refund is paid to; the approval covers it.</param>
public sealed record RequestRefund(Guid AccountId, decimal Amount, string ReasonCode, Guid BankAccountId) : ICommand<RefundView>;

public sealed record GetAccount(Guid AccountId) : IQuery<AccountView>;

public sealed record GetInvoice(Guid InvoiceId) : IQuery<InvoiceView>;

public sealed record ListInvoices(Guid AccountId) : IQuery<IReadOnlyList<InvoiceView>>;

public sealed record GetRefund(Guid RefundId) : IQuery<RefundView>;

public sealed record AccountsDue(DateOnly Day, bool ForInvoicing) : IQuery<IReadOnlyList<Guid>>;

public sealed class OpenAccountValidator : AbstractValidator<OpenAccount>
{
    public OpenAccountValidator()
    {
        RuleFor(c => c.PayerPartyId).NotEmpty();
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.CollectionMethod).NotEmpty().MaximumLength(15);
        RuleFor(c => c.MandateRef).MaximumLength(60);
    }
}

public sealed class CreateScheduleValidator : AbstractValidator<CreateSchedule>
{
    public CreateScheduleValidator()
    {
        RuleFor(c => c.SourceDomain).Must(d => d is "LIFE" or "GENERAL" or "HEALTH").WithMessage("The source domain is LIFE, GENERAL or HEALTH.");
        RuleFor(c => c.PolicyRef).NotEmpty();
        RuleFor(c => c.ProductVersionRef).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Frequency).NotEmpty().MaximumLength(10);
        RuleFor(c => c.InstalmentAmount).GreaterThan(0).PrecisionScale(18, 2, true);
    }
}

public sealed class RaiseCreditNoteValidator : AbstractValidator<RaiseCreditNote>
{
    public RaiseCreditNoteValidator()
    {
        RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(15);
    }
}

public sealed class RequestRefundValidator : AbstractValidator<RequestRefund>
{
    public RequestRefundValidator()
    {
        RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(15);
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

/// <summary>DetectArrears from an account's unpaid invoices past their due date: the oldest decides the days overdue.</summary>
internal static class Ageing
{
    public static bool HasOpenCase(BillingAccount account) => account.Arrears.Any(a => a.Status == "OPEN");

    public static async Task<Result> Detect(BillingAccount account, IReadOnlyList<Invoice> invoices, DateOnly today, IBillingCodes codes, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var overdue = invoices.Where(i => i.Outstanding > 0 && i.DueDate < today).ToList();
        var days = DaysOverdue(overdue, today);
        return account.DetectArrears(days, overdue.Sum(i => i.Outstanding), await codes.AgeingBuckets(today, cancellationToken), now);
    }

    public static int DaysOverdue(IReadOnlyList<Invoice> overdue, DateOnly today) => overdue.Count == 0 ? 0 : today.DayNumber - overdue.Min(i => i.DueDate).DayNumber;
}

internal static class Loaded
{
    public static async Task<Result<BillingAccount>> Account(IBillingRepository billing, Guid id, CancellationToken cancellationToken) =>
        await billing.Account(id, cancellationToken) is { } account ? account : Error.NotFound(ErrorCodes.NotFound, "The billing account was not found.");
}

internal sealed class OpenAccountHandler(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit, IExecutionContext execution) : ICommandHandler<OpenAccount, AccountView>
{
    public async Task<Result<AccountView>> Handle(OpenAccount command, CancellationToken cancellationToken)
    {
        if (!await codes.CollectionMethodExists(command.CollectionMethod, businessDate.Today, cancellationToken))
        {
            return Error.Validation(BillingErrorCodes.CodeNotInForce, $"{command.CollectionMethod} is not a collection method in force.");
        }

        var account = BillingAccount.OpenAccount(Guid.CreateVersion7(), command.PayerPartyId, command.Currency, command.CollectionMethod, command.MandateRef, clock.UtcNow);
        billing.Add(account);
        audit.Record(new AuditEntry("BILLING", "OpenAccount", nameof(BillingAccount), account.Id.ToString()));
        return AccountView.From(account, execution.CurrentScope);
    }
}

internal sealed class CreateScheduleHandler(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IAuditTrail audit, IExecutionContext execution) : ICommandHandler<CreateSchedule, AccountView>
{
    public async Task<Result<AccountView>> Handle(CreateSchedule command, CancellationToken cancellationToken)
    {
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        if (AccountScopes.RequireWhole(account.Value, execution) is { IsFailure: true } partly)
        {
            return partly.Error;
        }

        if (await codes.Frequency(command.Frequency, businessDate.Today, cancellationToken) is not { } frequency)
        {
            return Error.Validation(BillingErrorCodes.CodeNotInForce, $"{command.Frequency} is not a billing frequency in force.");
        }

        audit.Record(new AuditEntry("BILLING", "CreateSchedule", nameof(BillingAccount), account.Value.Id.ToString(),
            new Dictionary<string, string?> { ["policy"] = command.PolicyRef.ToString() }));
        var schedule = account.Value.CreateSchedule(command.SourceDomain, command.PolicyRef, command.ProductVersionRef, frequency, command.StartDate, command.EndDate,
            command.InstalmentAmount);
        return schedule.IsFailure ? schedule.Error : AccountView.From(account.Value, execution.CurrentScope);
    }
}

internal sealed class AmendScheduleHandler(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IAuditTrail audit, IExecutionContext execution) : ICommandHandler<AmendSchedule, AccountView>
{
    public async Task<Result<AccountView>> Handle(AmendSchedule command, CancellationToken cancellationToken)
    {
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        var schedule = account.Value.Schedules.SingleOrDefault(s => s.Id == command.ScheduleId);
        if (schedule is not null && !execution.CurrentScope.Allows(schedule.ProductCode, schedule.OrgNodeCode))
        {
            return Error.Forbidden(ErrorCodes.OutOfScope, "The schedule bills a policy outside your scope.");
        }

        if (schedule is null || await codes.Frequency(schedule.Frequency, businessDate.Today, cancellationToken) is not { } frequency)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The schedule, or its frequency in force, was not found.");
        }

        audit.Record(new AuditEntry("BILLING", "AmendSchedule", nameof(BillingAccount), account.Value.Id.ToString()));
        var result = account.Value.AmendSchedule(command.ScheduleId, frequency, command.InstalmentAmount, command.EffectiveFrom);
        return result.IsFailure ? result.Error : AccountView.From(account.Value, execution.CurrentScope);
    }
}

internal sealed class IssueDueInvoicesHandler(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IClock clock, IExecutionContext execution) : ICommandHandler<IssueDueInvoices, IReadOnlyList<InvoiceView>>
{
    public async Task<Result<IReadOnlyList<InvoiceView>>> Handle(IssueDueInvoices command, CancellationToken cancellationToken)
    {
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        if (AccountScopes.RequireWhole(account.Value, execution) is { IsFailure: true } partly)
        {
            return partly.Error;
        }

        var issued = await DueInvoices.Issue(account.Value, command.AsOf, billing, codes, clock, businessDate, cancellationToken);
        return issued.IsFailure ? issued.Error : issued.Value.Select(InvoiceView.From).ToList();
    }
}

/// <summary>Invoices for the instalments due by a day, one per schedule (BIL-002).</summary>
public static class DueInvoices
{
    /// <param name="firstBilling">
    /// True when a policy's schedule is billed for the first time or resumed (issue, endorsement, reinstatement, enrolment):
    /// instalments already past are due from today. False for the daily run, where each is due on its own date.
    /// </param>
    public static async Task<Result<IReadOnlyList<Invoice>>> Issue(BillingAccount account, DateOnly asOf, IBillingRepository billing, IBillingCodes codes, IClock clock,
        IBusinessDate businessDate, CancellationToken cancellationToken, bool firstBilling = false)
    {
        var prefix = await codes.NumberPrefix("INVOICE", businessDate.Today, cancellationToken);
        var paymentMethod = await codes.PaymentMethodOf(account.CollectionMethod, businessDate.Today, cancellationToken) ?? account.CollectionMethod;
        var issued = new List<Invoice>();
        foreach (var group in account.DueInstalments(asOf).GroupBy(d => d.Schedule).ToList())
        {
            var instalments = group.Select(g => g.Instalment).ToList();
            var invoice = Invoice.IssueInvoice(Guid.CreateVersion7(), prefix, account, group.Key, instalments, paymentMethod, businessDate.Today, clock.UtcNow,
                firstBilling ? businessDate.Today : null);
            if (invoice.IsFailure)
            {
                return invoice.Error;
            }

            foreach (var instalment in instalments)
            {
                account.MarkInvoiced(instalment, invoice.Value.Id);
            }

            billing.Add(invoice.Value);
            issued.Add(invoice.Value);
        }

        return issued;
    }
}

internal sealed class RaiseCreditNoteHandler(IBillingRepository billing, IBillingCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<RaiseCreditNote, InvoiceView>
{
    public async Task<Result<InvoiceView>> Handle(RaiseCreditNote command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await billing.Invoice(command.InvoiceId, cancellationToken) is not { } invoice)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The invoice was not found.");
        }

        if (await codes.AdjustmentReason(command.ReasonCode, businessDate.Today, cancellationToken) is not { } reason)
        {
            return Error.Validation(BillingErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not an adjustment reason in force.");
        }

        if (reason.RequiresApproval)
        {
            // TODO(spec): credit notes whose reason requires approval go through Workflow; not built yet (OQ-058).
            return Error.Validation(BillingErrorCodes.CodeNotInForce, $"Credit notes for reason {reason.Code} need an approval, which is not available yet.");
        }

        audit.Record(new AuditEntry("BILLING", "RaiseCreditNote", nameof(Invoice), invoice.Id.ToString(),
            new Dictionary<string, string?> { ["reason"] = reason.Code, ["amount"] = command.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        var credit = invoice.RaiseCreditNote(Guid.CreateVersion7(), await codes.NumberPrefix("CREDIT_NOTE", businessDate.Today, cancellationToken), command.Amount, reason,
            user.Value, businessDate.Today, clock.UtcNow);
        if (credit.IsFailure)
        {
            return credit.Error;
        }

        billing.Add(credit.Value);
        return InvoiceView.From(credit.Value);
    }
}

internal sealed class AgeAccountHandler(IBillingRepository billing, IBillingCodes codes, IClock clock, IExecutionContext execution) : ICommandHandler<AgeAccount, AccountView>
{
    public async Task<Result<AccountView>> Handle(AgeAccount command, CancellationToken cancellationToken)
    {
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        if (AccountScopes.RequireWhole(account.Value, execution) is { IsFailure: true } partly)
        {
            return partly.Error;
        }

        var invoices = await billing.Invoices(account.Value.Id, cancellationToken);
        var newlyOverdue = invoices.Count(i => i.MarkOverdue(command.Today, clock.UtcNow));
        var result = await Ageing.Detect(account.Value, invoices, command.Today, codes, clock.UtcNow, cancellationToken);

        // Request 46: with no bucket for the days overdue, a case not yet open is opened from InvoiceOverdue, which parks
        // until a bucket is in force (ADR-022). The overdue invoices are kept rather than rolled back with the refusal.
        if (result is { IsFailure: true, Error.Code: BillingErrorCodes.NoAgeingBucket } && newlyOverdue > 0 && !Ageing.HasOpenCase(account.Value))
        {
            return AccountView.From(account.Value, execution.CurrentScope);
        }

        return result.IsFailure ? result.Error : AccountView.From(account.Value, execution.CurrentScope);
    }
}

internal sealed class CloseAccountHandler(IBillingRepository billing, IAuditTrail audit, IExecutionContext execution) : ICommandHandler<CloseAccount, AccountView>
{
    public async Task<Result<AccountView>> Handle(CloseAccount command, CancellationToken cancellationToken)
    {
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (account.IsFailure)
        {
            return account.Error;
        }

        if (AccountScopes.RequireWhole(account.Value, execution) is { IsFailure: true } partly)
        {
            return partly.Error;
        }

        audit.Record(new AuditEntry("BILLING", "CloseAccount", nameof(BillingAccount), account.Value.Id.ToString()));
        var open = (await billing.Invoices(account.Value.Id, cancellationToken)).Any(i => i.Outstanding > 0);
        var result = account.Value.CloseAccount(open);
        return result.IsFailure ? result.Error : AccountView.From(account.Value, execution.CurrentScope);
    }
}

internal sealed class RequestRefundHandler(IBillingRepository billing, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution, IClock clock,
    IAuditTrail audit) : ICommandHandler<RequestRefund, RefundView>
{
    public async Task<Result<RefundView>> Handle(RequestRefund command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var account = await Loaded.Account(billing, command.AccountId, cancellationToken);
        if (user.IsFailure || account.IsFailure)
        {
            return user.IsFailure ? user.Error : account.Error!;
        }

        if (AccountScopes.RequireWhole(account.Value, execution) is { IsFailure: true } partly)
        {
            return partly.Error;
        }

        // Refunded only to the payer's own verified, current account in the account's currency.
        if (await bankAccounts.Get(tenant.RequireTenantId(), command.BankAccountId, cancellationToken) is not { IsPayable: true } bank
            || bank.PartyId != account.Value.PayerPartyId || bank.Currency != account.Value.Currency)
        {
            return Error.Validation(BillingErrorCodes.BankAccountNotPayable, "The bank account is not the payer's verified, current account in the account's currency.");
        }

        var refund = RefundRequest.RequestRefund(Guid.CreateVersion7(), account.Value, command.Amount, command.ReasonCode, bank.Id, bank.AccountNumberHash, user.Value,
            clock.UtcNow);
        if (refund.IsFailure)
        {
            return refund.Error;
        }

        billing.Add(refund.Value);
        audit.Record(new AuditEntry("BILLING", "RequestRefund", nameof(RefundRequest), refund.Value.Id.ToString()));
        return Views.From(refund.Value);
    }
}

internal sealed class GetAccountHandler(IBillingRepository billing, IExecutionContext execution) : IQueryHandler<GetAccount, AccountView>
{
    public async Task<Result<AccountView>> Handle(GetAccount query, CancellationToken cancellationToken) =>
        await billing.Account(query.AccountId, cancellationToken) is { } a ? AccountView.From(a, execution.CurrentScope) : Error.NotFound(ErrorCodes.NotFound, "The billing account was not found.");
}

internal sealed class GetInvoiceHandler(IBillingRepository billing) : IQueryHandler<GetInvoice, InvoiceView>
{
    public async Task<Result<InvoiceView>> Handle(GetInvoice query, CancellationToken cancellationToken) =>
        await billing.Invoice(query.InvoiceId, cancellationToken) is { } i ? InvoiceView.From(i) : Error.NotFound(ErrorCodes.NotFound, "The invoice was not found.");
}

/// <summary>An account's invoices: those in the caller's scope, of an account in scope (F103).</summary>
internal sealed class ListInvoicesHandler(IBillingRepository billing, IExecutionContext execution) : IQueryHandler<ListInvoices, IReadOnlyList<InvoiceView>>
{
    public async Task<Result<IReadOnlyList<InvoiceView>>> Handle(ListInvoices query, CancellationToken cancellationToken)
    {
        if (!execution.CurrentScope.IsUnrestricted && await billing.Account(query.AccountId, cancellationToken) is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The billing account was not found.");
        }

        return (await billing.Invoices(query.AccountId, cancellationToken)).Where(i => execution.CurrentScope.Allows(i.ProductCode, i.OrgNodeCode))
            .Select(InvoiceView.From).ToList();
    }
}

internal sealed class GetRefundHandler(IBillingRepository billing) : IQueryHandler<GetRefund, RefundView>
{
    public async Task<Result<RefundView>> Handle(GetRefund query, CancellationToken cancellationToken) =>
        await billing.Refund(query.RefundId, cancellationToken) is { } r ? Views.From(r) : Error.NotFound(ErrorCodes.NotFound, "The refund was not found.");
}

internal sealed class AccountsDueHandler(IBillingRepository billing) : IQueryHandler<AccountsDue, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(AccountsDue query, CancellationToken cancellationToken) =>
        Result.Success(query.ForInvoicing
            ? await billing.AccountsWithDueInstalments(query.Day, 500, cancellationToken)
            : await billing.AccountsToAge(query.Day, 500, cancellationToken));
}

internal static class Views
{
    public static RefundView From(RefundRequest r) => new(r.Id, r.BillingAccountId, r.Amount, r.Currency, r.ReasonCode, r.Status, r.PaymentId, r.RequestedBy,
        r.DecidedBy, r.BankAccountId);
}

/// <summary>
/// Billing's policies: payments allocated mark invoices settled (BIL-003); a reversed payment reopens them; Workflow's
/// decision on a refund; a succeeded refund payment marks the refund paid.
/// </summary>
public sealed class BillingPolicies(IBillingRepository billing, IBusinessDate businessDate, IClock clock) :
    IIntegrationEventHandler<PaymentAllocated>,
    IIntegrationEventHandler<PaymentReversed>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentInitiated>,
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>
{
    public async Task Handle(PaymentAllocated allocated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await billing.Invoice(allocated.InvoiceId, cancellationToken) is { } invoice)
        {
            // TODO(spec): an allocation above what is outstanding is not applied; overpayment handling is the allocation rule's (OQ-058).
            invoice.ApplyAllocation(allocated.Amount, clock.UtcNow);
        }
    }

    public async Task Handle(PaymentReversed reversed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var allocation in reversed.ReversedAllocations)
        {
            if (await billing.Invoice(allocation.InvoiceId, cancellationToken) is { } invoice)
            {
                invoice.ReverseAllocation(allocation.Amount, businessDate.Today);
            }
        }
    }

    /// <summary>GAP-036: the refund shows its payout's ID from when the payout is created.</summary>
    public async Task Handle(PaymentInitiated initiated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (initiated.BusinessRefType == BillingApprovals.RefundBusinessRefType && initiated.BusinessRefId is { } refundId
            && await billing.Refund(refundId, cancellationToken) is { } refund)
        {
            refund.LinkPayment(initiated.PaymentId);
        }
    }

    public async Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (succeeded.Purpose == PaymentPurposes.Refund && succeeded.BusinessRefType == BillingApprovals.RefundBusinessRefType
            && succeeded.BusinessRefId is { } refundId && await billing.Refund(refundId, cancellationToken) is { } refund)
        {
            refund.MarkPaid(succeeded.PaymentId);
        }
    }

    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        DecideRefund(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.CheckerId, granted.SubjectHash, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        DecideRefund(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.CheckerId, rejected.SubjectHash, cancellationToken);

    private async Task DecideRefund(string businessRefType, Guid refundId, bool approved, Guid checkerId, string? subjectHash, CancellationToken cancellationToken)
    {
        if (businessRefType == BillingApprovals.RefundBusinessRefType && await billing.Refund(refundId, cancellationToken) is { } refund)
        {
            refund.Decide(approved, checkerId, subjectHash, clock.UtcNow);
        }
    }
}
