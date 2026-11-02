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
using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Finance.Domain.Ledger;
using Insys.Tenancy.Contracts;

namespace Insys.Finance.Application;

public sealed record OpeningBalanceLine(string AccountCode, decimal Debit, decimal Credit);

/// <summary>B-013: the source system's trial balance on cut-over, as one balanced journal in the tenant's base currency. A batch has one.</summary>
public sealed record MigrateOpeningBalances(string BatchCode, DateOnly AccountingDate, decimal ControlDebit, decimal ControlCredit, string Currency, IReadOnlyList<OpeningBalanceLine> Lines) : ICommand<OpeningBalancesResult>;

public sealed record OpeningBalanceRejectedLine(int Line, string Reason);

public sealed record OpeningBalancesResult(Guid? JournalId, IReadOnlyList<OpeningBalanceRejectedLine> RejectedLines, string? Currency = null);

public sealed class MigrateOpeningBalancesValidator : AbstractValidator<MigrateOpeningBalances>
{
    public MigrateOpeningBalancesValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Lines).NotEmpty();
    }
}

internal sealed class MigrateOpeningBalancesHandler(IJournalRepository journals, IAccountingPeriods periods, IPostingConfiguration configuration, ITenantBaseCurrency baseCurrency, ITenantContext tenant, IClock clock, IAuditTrail audit)
    : ICommandHandler<MigrateOpeningBalances, OpeningBalancesResult>
{
    public async Task<Result<OpeningBalancesResult>> Handle(MigrateOpeningBalances command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var day = command.AccountingDate;
        if (await baseCurrency.BaseCurrency(tenantId, day, cancellationToken) is not { } currency)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The tenant has no base currency on that date.");
        }

        var rejected = new List<OpeningBalanceRejectedLine>();
        if (!string.Equals(currency, command.Currency, StringComparison.OrdinalIgnoreCase))
        {
            rejected.Add(new OpeningBalanceRejectedLine(0, $"An opening balance is in the tenant's base currency, {currency}, not {command.Currency}."));
        }

        if (command.ControlDebit != command.Lines.Sum(l => l.Debit) || command.ControlCredit != command.Lines.Sum(l => l.Credit))
        {
            rejected.Add(new OpeningBalanceRejectedLine(0, $"The lines total {command.Lines.Sum(l => l.Debit)} debit and {command.Lines.Sum(l => l.Credit)} credit; the control totals say {command.ControlDebit} and {command.ControlCredit}."));
        }

        for (var i = 0; i < command.Lines.Count; i++)
        {
            var line = command.Lines[i];
            if (!await configuration.AccountUsable(line.AccountCode, currency, day, cancellationToken))
            {
                rejected.Add(new OpeningBalanceRejectedLine(i + 1, $"{line.AccountCode} isn't an account usable in {currency}."));
            }
        }

        var source = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("migration:" + command.BatchCode))[..16]);
        if (await journals.BySourceEvent(source, cancellationToken) is not null)
        {
            rejected.Add(new OpeningBalanceRejectedLine(0, "This batch's opening balances are already posted."));
        }

        var id = Guid.CreateVersion7();
        audit.Record(new AuditEntry("FINANCE", "MigrateOpeningBalances", nameof(Journal), command.BatchCode, new Dictionary<string, string?>
        {
            ["lines"] = command.Lines.Count.ToString(CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new OpeningBalancesResult(null, rejected, currency);
        }

        if (await periods.Covering(day, cancellationToken) is not { } period)
        {
            return Error.Conflict(FinanceErrorCodes.PeriodNotOpen, $"No accounting period covers {day:yyyy-MM-dd}.");
        }

        var journal = Journal.OpeningBalances(id, source, command.BatchCode, day, period, currency, command.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).ToList(), clock.UtcNow);
        if (journal.IsFailure)
        {
            return journal.Error;
        }

        journals.Add(journal.Value);
        return new OpeningBalancesResult(id, [], currency);
    }
}
