/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Members;

namespace Insys.Health.Application;

// OQ-158, B-013: a batch of in-force memberships from a source system. Benefit use to date isn't brought (see the batch report).

public sealed record MigratedDependant(Guid PartyId, string DependantType);

public sealed record MigratedMembership(string SourceReference, Guid PrincipalPartyId, Guid? PayerPartyId, Guid? SchemeId, string? MemberCategory, [property: CodeList(HealthCodeLists.PlansAndOptions, "plan_code")] string PlanCode, [property: CodeList(HealthCodeLists.PlansAndOptions, "option_code")] string OptionCode, DateOnly StartDate, string Currency, [property: CodeList(HealthCodeLists.CollectionMethods, "method_code")] string CollectionMethod,
    decimal MonthlyContribution, decimal EmployerContribution, Guid? SponsorPartyId, IReadOnlyList<MigratedDependant>? Dependants);

/// <param name="ControlContribution">The source system's total monthly contribution, members' and employers'.</param>
public sealed record MigrateHealthMemberships(string BatchCode, int ControlCount, decimal ControlContribution, IReadOnlyList<MigratedMembership> Rows) : ICommand<HealthMigrationResult>;

public sealed record HealthMigrationRejectedLine(int Line, string Reason);

public sealed record HealthMigrationResult(int Loaded, decimal LoadedContribution, IReadOnlyList<HealthMigrationRejectedLine> RejectedLines, IReadOnlyDictionary<string, Guid>? Memberships = null);

public sealed class MigrateHealthMembershipsValidator : AbstractValidator<MigrateHealthMemberships>
{
    public MigrateHealthMembershipsValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateHealthMembershipsHandler(IHealthRepository health, IHealthCodes codes, Party.Contracts.IPartyDirectory parties, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<MigrateHealthMemberships, HealthMigrationResult>
{
    public async Task<Result<HealthMigrationResult>> Handle(MigrateHealthMemberships command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var today = businessDate.Today;
        var total = command.Rows.Sum(r => r.MonthlyContribution + r.EmployerContribution);
        var rejected = new List<HealthMigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new HealthMigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlContribution != total)
        {
            rejected.Add(new HealthMigrationRejectedLine(0, $"The file's contribution is {total}; its control total says {command.ControlContribution}."));
        }

        if (await codes.MemberNumberPrefix(today, cancellationToken) is not { } prefix)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, "No member numbering scheme is in force.");
        }

        var known = (await health.MembershipSourceReferencesKnown(command.Rows.Select(r => r.SourceReference).ToList(), cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plans = new List<PlanDefinition>();
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

            if (row.Currency.Length != 3 || row.MonthlyContribution < 0 || row.EmployerContribution < 0 || (row.EmployerContribution > 0 && row.SponsorPartyId is null))
            {
                reasons.Add("A membership has a currency and contributions that aren't negative; an employer's share names its sponsor.");
            }

            if (await codes.Plan(row.PlanCode, row.OptionCode, today, cancellationToken) is { } plan)
            {
                plans.Add(plan);
            }
            else
            {
                reasons.Add($"{row.PlanCode}/{row.OptionCode} isn't a plan option in force.");
            }

            var named = new[] { row.PrincipalPartyId, row.PayerPartyId ?? row.PrincipalPartyId, row.SponsorPartyId ?? row.PrincipalPartyId }.Concat((row.Dependants ?? []).Select(d => d.PartyId)).Distinct();
            foreach (var party in named)
            {
                if (!await parties.Exists(tenantId, party, cancellationToken))
                {
                    reasons.Add("A party named isn't a party of the tenant.");
                    break;
                }
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new HealthMigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("HEALTH", "MigrateHealthMemberships", nameof(HealthMembership), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new HealthMigrationResult(0, 0, rejected);
        }

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < command.Rows.Count; i++)
        {
            var row = command.Rows[i];
            var id = ids[row.SourceReference] = Guid.CreateVersion7();
            health.Add(HealthMembership.Migrate(id, Numbers.Of(prefix, id, 12), command.BatchCode, row.SourceReference, row.PrincipalPartyId, row.SchemeId, row.MemberCategory, new PlanOption(row.PlanCode, row.OptionCode, plans[i].Version), row.StartDate,
                row.PayerPartyId ?? row.PrincipalPartyId, Guid.CreateVersion7(), row.CollectionMethod, row.Currency.ToUpperInvariant(), row.MonthlyContribution, row.EmployerContribution, row.SponsorPartyId,
                (row.Dependants ?? []).Select(d => (d.PartyId, d.DependantType)).ToList(), plans[i].ProductCode, plans[i].Version, today, clock.UtcNow));
        }

        return new HealthMigrationResult(command.Rows.Count, total, [], ids);
    }
}

/// <param name="MembershipId">The membership's id here, as its migration returned it.</param>
/// <param name="BeneficiaryPartyId">The member; for a FAMILY limit, the principal.</param>
/// <param name="Lifetime">Otherwise the benefit year holding today, by the plan's benefit year: the period adjudication will look for.</param>
/// <param name="Limit">The limit of the period, as the plan's limit for that scope; null for a member's own running total with none.</param>
public sealed record OpeningAccumulator(string SourceReference, Guid MembershipId, Guid BeneficiaryPartyId, string BenefitCategory, [property: OneOf(AccumulatorScope.Member, AccumulatorScope.Family)] string Scope, bool Lifetime, decimal? Limit, decimal Used,
    decimal DeductibleMet, decimal CopayTaken);

/// <summary>Ade, 1 November 2026: a member's benefit use to date, so a migrated member can't claim the period's benefit twice. Before any claim on the membership, after the batch's memberships.</summary>
public sealed record MigrateHealthAccumulators(string BatchCode, int ControlCount, decimal ControlUsed, IReadOnlyList<OpeningAccumulator> Rows) : ICommand<HealthMigrationResult>;

public sealed class MigrateHealthAccumulatorsValidator : AbstractValidator<MigrateHealthAccumulators>
{
    public MigrateHealthAccumulatorsValidator()
    {
        RuleFor(c => c.BatchCode).NotEmpty().MaximumLength(30);
        RuleFor(c => c.Rows).NotEmpty().Must(r => r.Count <= 2000).WithMessage("A file has at most 2000 rows.");
    }
}

internal sealed class MigrateHealthAccumulatorsHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IAuditTrail audit) : ICommandHandler<MigrateHealthAccumulators, HealthMigrationResult>
{
    public async Task<Result<HealthMigrationResult>> Handle(MigrateHealthAccumulators command, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var total = command.Rows.Sum(r => r.Used);
        var rejected = new List<HealthMigrationRejectedLine>();
        if (command.ControlCount != command.Rows.Count)
        {
            rejected.Add(new HealthMigrationRejectedLine(0, $"The file has {command.Rows.Count} rows; its control count says {command.ControlCount}."));
        }

        if (command.ControlUsed != total)
        {
            rejected.Add(new HealthMigrationRejectedLine(0, $"The file's use is {total}; its control total says {command.ControlUsed}."));
        }

        var seen = new HashSet<(Guid, AccumulatorKey)>();
        var ready = new List<(OpeningAccumulator Row, AccumulatorKey Key)>();
        for (var i = 0; i < command.Rows.Count; i++)
        {
            var row = command.Rows[i];
            var reasons = new List<string>();
            if (string.IsNullOrWhiteSpace(row.SourceReference) || row.SourceReference.Length > 40 || string.IsNullOrWhiteSpace(row.BenefitCategory) || row.BenefitCategory.Length > 15)
            {
                reasons.Add("A row has a source reference of at most 40 characters and a benefit category of at most 15.");
            }

            if (row.Used < 0 || row.DeductibleMet < 0 || row.CopayTaken < 0 || (row.Limit is { } limit && (limit < 0 || row.Used > limit)))
            {
                reasons.Add("Amounts aren't negative, and what's used is within the limit.");
            }

            var membership = await health.Membership(row.MembershipId, cancellationToken);
            if (membership?.MigrationBatch is null)
            {
                reasons.Add("That isn't a migrated membership: load the batch's memberships first.");
            }
            else if (row.BeneficiaryPartyId != membership.PrincipalPartyId && membership.Dependants.All(d => d.PartyId != row.BeneficiaryPartyId))
            {
                reasons.Add("The beneficiary isn't on the membership.");
            }
            else if (await codes.Plan(membership.PlanCode, membership.OptionCode, today, cancellationToken) is not { } plan)
            {
                reasons.Add($"{membership.PlanCode}/{membership.OptionCode} isn't a plan option in force.");
            }
            else
            {
                var (start, end) = row.Lifetime ? BenefitPeriods.Lifetime(membership.StartDate) : BenefitPeriods.Year(plan.Terms.BenefitYear, membership.StartDate, today);
                var key = new AccumulatorKey(row.Scope, row.BeneficiaryPartyId, row.BenefitCategory, start, end);
                if (!seen.Add((row.MembershipId, key)) || (await health.Accumulators(row.MembershipId, [key], cancellationToken)).Count > 0)
                {
                    reasons.Add("This accumulator is already loaded, or appears earlier in the file.");
                }
                else
                {
                    ready.Add((row, key));
                }
            }

            if (reasons.Count > 0)
            {
                rejected.Add(new HealthMigrationRejectedLine(i + 1, string.Join("; ", reasons)));
            }
        }

        audit.Record(new AuditEntry("HEALTH", "MigrateHealthAccumulators", nameof(Domain.Benefits.BenefitAccumulator), command.BatchCode, new Dictionary<string, string?>
        {
            ["rows"] = command.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["rejected"] = rejected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));
        if (rejected.Count > 0)
        {
            return new HealthMigrationResult(0, 0, rejected);
        }

        foreach (var (row, key) in ready)
        {
            health.Add(Domain.Benefits.BenefitAccumulator.Opening(Guid.CreateVersion7(), row.MembershipId, key, row.Limit, row.Used, row.DeductibleMet, row.CopayTaken, command.BatchCode, row.SourceReference));
        }

        return new HealthMigrationResult(command.Rows.Count, total, []);
    }
}
