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
using System.Globalization;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Billing.Domain.Billing;

namespace Insys.Billing.Application;

/// <param name="SourceDomain">GENERAL, LIFE or HEALTH: the source domain of the policy's schedule.</param>
/// <param name="PolicyRef">The policy's or membership's id here, as its migration returned it.</param>
public sealed record OutstandingPremium(string SourceReference, [property: OneOf("GENERAL", "LIFE", "HEALTH")] string SourceDomain, Guid PolicyRef, decimal Amount, DateOnly DueDate);

/// <summary>B-013: premiums invoiced in the source system and unpaid, after the batch's policies have been loaded and Billing has opened their accounts.</summary>
public sealed record MigrateOutstandingPremiums(string BatchCode, int ControlCount, decimal ControlAmount, IReadOnlyList<OutstandingPremium> Rows) : ICommand<PremiumMigrationResult>;

public sealed record PremiumMigrationRejectedLine(int Line, string Reason);

public sealed record PremiumMigrationResult(int Loaded, decimal LoadedAmount, IReadOnlyList<PremiumMigrationRejectedLine> RejectedLines);

public sealed class MigrateOutstandingPremiumsValidator : AbstractValidator<MigrateOutstandingPremiums>
{
    public MigrateOutstandingPremiumsValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateOutstandingPremiumsHandler(IBillingRepository billing, IBillingCodes codes, IBusinessDate businessDate, IAuditTrail audit) : ICommandHandler<MigrateOutstandingPremiums, PremiumMigrationResult>
{
    public async Task<Result<PremiumMigrationResult>> Handle(MigrateOutstandingPremiums command, CancellationToken cancellationToken)
    {
        var total = command.Rows.Sum(r => r.Amount);
        var rejected = new List<PremiumMigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new PremiumMigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlAmount != total)
        {
            rejected.Add(new PremiumMigrationRejectedLine(0, $"The file's amount is {total}; its control total says {command.ControlAmount}."));
        }

        var known = (await billing.InvoiceSourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ready = new List<(OutstandingPremium Row, BillingAccount Account, PremiumSchedule Schedule)>();
        for (var i = 0; i < command.Rows.Count; i++)
        {
            var row = command.Rows[i];
            var reasons = new List<string>();
            if (string.IsNullOrWhiteSpace(row.SourceReference) || row.SourceReference.Length > 40)
            {
                reasons.Add("The source reference is required, at most 40 characters.");
            }
            else if (known.Contains(row.SourceReference) || !seen.Add(row.SourceReference))
            {
                reasons.Add("This source reference is already loaded, or appears earlier in the file.");
            }

            if (row.Amount <= 0 || decimal.Round(row.Amount, 2) != row.Amount)
            {
                reasons.Add("An amount is positive, to two decimal places.");
            }

            if (await billing.AccountForPolicy(row.SourceDomain, row.PolicyRef, cancellationToken) is { } account && account.ScheduleFor(row.SourceDomain, row.PolicyRef) is { } schedule)
            {
                ready.Add((row, account, schedule));
            }
            else
            {
                reasons.Add("The policy has no billing account yet: load the batch's policies first.");
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new PremiumMigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("BILLING", "MigrateOutstandingPremiums", nameof(Invoice), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new PremiumMigrationResult(0, 0, rejected);
        }

        var prefix = await codes.NumberPrefix("INVOICE", businessDate.Today, cancellationToken);
        foreach (var (row, account, schedule) in ready)
        {
            billing.Add(Invoice.Migrated(Guid.CreateVersion7(), prefix, account, schedule, row.Amount, row.DueDate, businessDate.Today, command.BatchCode, row.SourceReference));
        }

        return new PremiumMigrationResult(command.Rows.Count, total, []);
    }
}
