/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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
using System.Globalization;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Finance.Domain.Ledger;

namespace Insys.Finance.Application;

// F094 (addenda/finance-and-compliance.md §4.2).

public sealed record PrepareTaxReturn(string TaxCode, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd) : ICommand<TaxReturnView>;

public sealed record ApproveTaxReturn(Guid Id) : ICommand<TaxReturnView>;

public sealed record RejectTaxReturn(Guid Id, string Reason) : ICommand<TaxReturnView>;

public sealed record GetTaxReturn(Guid Id) : IQuery<TaxReturnView>;

public sealed record ListTaxReturns(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<TaxReturnView>>;

/// <summary>The approved return as a file, audited as it leaves. A command, so its audit entry is written.</summary>
public sealed record ExportTaxReturn(Guid Id) : ICommand<string>;

public sealed record TaxReturnView(Guid Id, string ReturnNumber, string TaxCode, string AccountCode, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd, decimal OutputTotal, decimal InputTotal,
    [property: OneOf(TaxReturnStatus.Prepared, TaxReturnStatus.Approved, TaxReturnStatus.Rejected)] string Status, Guid PreparedBy, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? RejectReason, long RowVersion)
{
    public static TaxReturnView From(TaxReturn r) => new(r.Id, r.ReturnNumber, r.TaxCode, r.AccountCode, r.Currency, r.PeriodStart, r.PeriodEnd, r.OutputTotal, r.InputTotal, r.Status, r.PreparedBy, r.DecidedBy,
        r.DecidedAt, r.RejectReason, r.RowVersion);

    /// <summary>The file. INSYS files nothing; finance to confirm this layout against the return's format.</summary>
    public static string Csv(TaxReturn r) => string.Create(CultureInfo.InvariantCulture,
        $"return_number,tax_code,currency,period_start,period_end,output_total,input_total\n{r.ReturnNumber},{r.TaxCode},{r.Currency},{r.PeriodStart:yyyy-MM-dd},{r.PeriodEnd:yyyy-MM-dd},{r.OutputTotal:0.00},{r.InputTotal:0.00}\n");
}

public interface ITaxReturns
{
    Task<TaxReturn?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaxReturn>> List(Guid? afterId, int limit, CancellationToken cancellationToken);

    /// <summary>A return for this tax code, currency and period that isn't rejected.</summary>
    Task<bool> Exists(string taxCode, string currency, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken);

    /// <summary>The tax code's account in force on the day (finance.cfg_tax_codes_on_financial_transactions).</summary>
    Task<string?> TaxAccount(string taxCode, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Credits less debits on the account in the currency, by accounting date; a reversal nets off.</summary>
    Task<decimal> NetCredits(string accountCode, string currency, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    void Add(TaxReturn taxReturn);
}

public sealed class PrepareTaxReturnValidator : AbstractValidator<PrepareTaxReturn>
{
    public PrepareTaxReturnValidator()
    {
        RuleFor(c => c.TaxCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.PeriodEnd).GreaterThanOrEqualTo(c => c.PeriodStart);
    }
}

public sealed class RejectTaxReturnValidator : AbstractValidator<RejectTaxReturn>
{
    public RejectTaxReturnValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

internal sealed class TaxReturnHandlers(ITaxReturns returns, IExecutionContext execution, IClock clock, IAuditTrail audit) :
    ICommandHandler<PrepareTaxReturn, TaxReturnView>,
    ICommandHandler<ApproveTaxReturn, TaxReturnView>,
    ICommandHandler<RejectTaxReturn, TaxReturnView>,
    ICommandHandler<ExportTaxReturn, string>,
    IQueryHandler<GetTaxReturn, TaxReturnView>,
    IQueryHandler<ListTaxReturns, IReadOnlyList<TaxReturnView>>
{
    private static Error NotFound => Error.NotFound(ErrorCodes.NotFound, "The tax return was not found.");

    public async Task<Result<TaxReturnView>> Handle(PrepareTaxReturn command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await returns.TaxAccount(command.TaxCode, command.PeriodEnd, cancellationToken) is not { } account)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"{command.TaxCode} is not a tax code in force on {command.PeriodEnd:yyyy-MM-dd}.");
        }

        if (await returns.Exists(command.TaxCode, command.Currency, command.PeriodStart, command.PeriodEnd, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "A return for this tax code and period already exists; reject it to prepare another.");
        }

        var id = Guid.CreateVersion7();
        var taxReturn = TaxReturn.Prepare(id, $"TR-{id:N}"[..25], command.TaxCode, account, command.Currency, command.PeriodStart, command.PeriodEnd,
            await returns.NetCredits(account, command.Currency, command.PeriodStart, command.PeriodEnd, cancellationToken), user.Value);
        returns.Add(taxReturn);
        audit.Record(new AuditEntry("FINANCE", "PrepareTaxReturn", nameof(TaxReturn), id.ToString()));
        return TaxReturnView.From(taxReturn);
    }

    public Task<Result<TaxReturnView>> Handle(ApproveTaxReturn command, CancellationToken cancellationToken) =>
        Decide(command.Id, "ApproveTaxReturn", (r, user) => r.Approve(user, clock.UtcNow), cancellationToken);

    public Task<Result<TaxReturnView>> Handle(RejectTaxReturn command, CancellationToken cancellationToken) =>
        Decide(command.Id, "RejectTaxReturn", (r, user) => r.Reject(command.Reason, user, clock.UtcNow), cancellationToken);

    private async Task<Result<TaxReturnView>> Decide(Guid id, string operation, Func<TaxReturn, Guid, Result> decide, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await returns.Get(id, cancellationToken) is not { } taxReturn)
        {
            return NotFound;
        }

        var decided = decide(taxReturn, user.Value);
        if (decided.IsFailure)
        {
            return decided.Error;
        }

        audit.Record(new AuditEntry("FINANCE", operation, nameof(TaxReturn), id.ToString()));
        return TaxReturnView.From(taxReturn);
    }

    public async Task<Result<string>> Handle(ExportTaxReturn command, CancellationToken cancellationToken)
    {
        if (await returns.Get(command.Id, cancellationToken) is not { } taxReturn)
        {
            return NotFound;
        }

        if (taxReturn.Status != TaxReturnStatus.Approved)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an approved return is exported.");
        }

        audit.Record(new AuditEntry("FINANCE", "ExportTaxReturn", nameof(TaxReturn), command.Id.ToString()));
        return TaxReturnView.Csv(taxReturn);
    }

    public async Task<Result<TaxReturnView>> Handle(GetTaxReturn query, CancellationToken cancellationToken) =>
        await returns.Get(query.Id, cancellationToken) is { } taxReturn ? TaxReturnView.From(taxReturn) : NotFound;

    public async Task<Result<IReadOnlyList<TaxReturnView>>> Handle(ListTaxReturns query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<TaxReturnView>>((await returns.List(query.AfterId, query.Limit, cancellationToken)).Select(TaxReturnView.From).ToList());
}
