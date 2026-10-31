/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

// OQ-158, B-013: a batch of in-force policies from a source system.

public sealed record MigratedBenefit(string BenefitCode, decimal SumAssured, decimal Premium);

public sealed record MigratedLife(Guid PartyId, string LifeRole, int EntryAge, IReadOnlyList<MigratedBenefit> Benefits);

public sealed record MigratedLifePolicy(string SourceReference, string ProductCode, int ProductVersion, Guid PolicyholderPartyId, Guid? PayerPartyId, DateOnly InceptionDate, DateOnly? TermEndDate, string Currency, string BillingFrequency, string CollectionMethod,
    string? IntermediaryCode, IReadOnlyList<MigratedLife> Lives, IReadOnlyList<BeneficiaryNomination>? Beneficiaries);

/// <param name="ControlPremium">The source system's total premium per instalment.</param>
public sealed record MigrateLifePolicies(string BatchCode, int ControlCount, decimal ControlPremium, IReadOnlyList<MigratedLifePolicy> Rows) : ICommand<LifeMigrationResult>;

public sealed record LifeMigrationRejectedLine(int Line, string Reason);

public sealed record LifeMigrationResult(int Loaded, decimal LoadedPremium, IReadOnlyList<LifeMigrationRejectedLine> RejectedLines);

public sealed class MigrateLifePoliciesValidator : AbstractValidator<MigrateLifePolicies>
{
    public MigrateLifePoliciesValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateLifePoliciesHandler(ILifeRepository life, ILifeCodes codes, ISellableProducts products, Party.Contracts.IPartyDirectory parties, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<MigrateLifePolicies, LifeMigrationResult>
{
    public async Task<Result<LifeMigrationResult>> Handle(MigrateLifePolicies command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var today = businessDate.Today;
        static decimal Premium(MigratedLifePolicy row) => row.Lives.SelectMany(l => l.Benefits).Sum(b => b.Premium);
        var total = command.Rows.Sum(Premium);
        var rejected = new List<LifeMigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new LifeMigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlPremium != total)
        {
            rejected.Add(new LifeMigrationRejectedLine(0, $"The file's premium is {total}; its control total says {command.ControlPremium}."));
        }

        var known = (await life.SourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
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

            if (row.TermEndDate is { } end && (end < today || end < row.InceptionDate))
            {
                reasons.Add("Only a policy whose term hasn't ended is migrated.");
            }

            if (row.Currency.Length != 3 || Premium(row) <= 0 || row.Lives.Count == 0 || row.Lives.Any(l => l.Benefits.Count == 0 || l.Benefits.Any(b => b.SumAssured <= 0 || b.Premium < 0)))
            {
                reasons.Add("A policy has a currency, a positive premium, and at least one life, each with a benefit and a sum assured.");
            }

            if (await products.Version(tenantId, row.ProductCode, row.ProductVersion, today, cancellationToken) is null)
            {
                reasons.Add($"{row.ProductCode} v{row.ProductVersion} isn't a published product version.");
            }

            foreach (var party in new[] { row.PolicyholderPartyId, row.PayerPartyId ?? row.PolicyholderPartyId }.Concat(row.Lives.Select(l => l.PartyId)).Concat((row.Beneficiaries ?? []).Select(b => b.PartyId)).Distinct())
            {
                if (!await parties.Exists(tenantId, party, cancellationToken))
                {
                    reasons.Add("A party named isn't a party of the tenant.");
                    break;
                }
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new LifeMigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("LIFE", "MigrateLifePolicies", nameof(LifePolicy), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new LifeMigrationResult(0, 0, rejected);
        }

        foreach (var row in command.Rows)
        {
            var id = Guid.CreateVersion7();
            var number = await Numbers.Policy(codes, id, today, cancellationToken);
            if (number.IsFailure)
            {
                return number.Error;
            }

            life.Add(LifePolicy.Migrate(id, number.Value, command.BatchCode, row.SourceReference, row.ProductCode, row.ProductVersion, row.PolicyholderPartyId, row.PayerPartyId ?? row.PolicyholderPartyId, Guid.CreateVersion7(), row.BillingFrequency,
                row.CollectionMethod, row.InceptionDate, row.TermEndDate, row.Currency.ToUpperInvariant(), row.IntermediaryCode,
                row.Lives.Select(l => (l.PartyId, l.LifeRole, l.EntryAge, (IReadOnlyList<(string, decimal, decimal)>)l.Benefits.Select(b => (b.BenefitCode, b.SumAssured, b.Premium)).ToList())).ToList(), row.Beneficiaries ?? [], today, clock.UtcNow));
        }

        return new LifeMigrationResult(command.Rows.Count, total, []);
    }
}
