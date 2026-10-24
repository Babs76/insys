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
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Members;
using Insys.ProductFactory.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.Health.Application;

public sealed record RequestAuthorisation(Guid MembershipId, Guid PatientPartyId, string ProviderId, IReadOnlyList<string> DiagnosisCodes, string ServiceType,
    IReadOnlyList<PreauthLineRequest> Lines) : ICommand<PreauthorisationView>;

public sealed record ReviewAuthorisation(Guid PreauthorisationId) : ICommand<PreauthorisationView>;

public sealed record LineAmount(Guid LineId, decimal AuthorisedAmount);

/// <summary>Command ApproveAuthorisation, or ApproveWithConditions when <paramref name="Conditions"/> are given.</summary>
public sealed record ApproveAuthorisation(Guid PreauthorisationId, IReadOnlyList<LineAmount> Lines, DateOnly ValidFrom, string? Conditions) : ICommand<PreauthorisationView>;

public sealed record RejectAuthorisation(Guid PreauthorisationId, string Reason) : ICommand<PreauthorisationView>;

public sealed record ModifyAuthorisation(Guid PreauthorisationId, Guid LineId, decimal AuthorisedAmount) : ICommand<PreauthorisationView>;

public sealed record CancelAuthorisation(Guid PreauthorisationId) : ICommand<PreauthorisationView>;

/// <summary>Command ConfirmConditionsMet: a conditional approval's conditions are met; it becomes Approved.</summary>
public sealed record ConfirmConditionsMet(Guid PreauthorisationId) : ICommand<PreauthorisationView>;

public sealed record GetAuthorisation(Guid PreauthorisationId) : IQuery<PreauthorisationView>;

public sealed record SubmitMedicalClaim(string Source, string? ProviderId, Guid MembershipId, Guid PatientPartyId, DateOnly ServiceDate, string FormatCode, string Currency,
    Guid? PreauthorisationId, IReadOnlyList<ClaimLineRequest> Lines) : ICommand<MedicalClaimView>;

public sealed record ValidateMedicalClaim(Guid ClaimId) : ICommand<MedicalClaimView>;

/// <summary>Command AdjudicateClaim. It is safe to repeat: on a concurrent change the server runs it again, up to a configured limit (ADR-012).</summary>
public sealed record AdjudicateMedicalClaim(Guid ClaimId) : ICommand<MedicalClaimView>, IRetryOnConflict;

public sealed record LineReview(int LineNo, ReviewDecision Decision);

/// <summary>"Information received": a reviewer accepts or rejects the pended lines, and the claim is adjudicated again.</summary>
public sealed record ReviewPendedClaim(Guid ClaimId, IReadOnlyList<LineReview> Reviews) : ICommand<MedicalClaimView>, IRetryOnConflict;

public sealed record ApproveMedicalClaim(Guid ClaimId) : ICommand<MedicalClaimView>;

public sealed record RejectMedicalClaim(Guid ClaimId, string Reason) : ICommand<MedicalClaimView>;

public sealed record ReverseMedicalClaim(Guid ClaimId, string Reason) : ICommand<MedicalClaimView>;

public sealed record GetMedicalClaim(Guid ClaimId) : IQuery<MedicalClaimView>;

public sealed class RequestAuthorisationValidator : AbstractValidator<RequestAuthorisation>
{
    public RequestAuthorisationValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ServiceType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Lines).NotEmpty();
        RuleForEach(c => c.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.ProcedureCode).NotEmpty().MaximumLength(10);
            l.RuleFor(x => x.Quantity).InclusiveBetween(1, 999);
            l.RuleFor(x => x.RequestedAmount).GreaterThan(0).PrecisionScale(18, 2, true);
        });
    }
}

public sealed class SubmitMedicalClaimValidator : AbstractValidator<SubmitMedicalClaim>
{
    public SubmitMedicalClaimValidator()
    {
        RuleFor(c => c.Source).Must(s => s is ClaimSources.Provider or ClaimSources.Member).WithMessage("The source is PROVIDER or MEMBER.");
        RuleFor(c => c.ProviderId).MaximumLength(15);
        RuleFor(c => c.FormatCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Lines).NotEmpty();
        RuleForEach(c => c.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.TariffCode).NotEmpty().MaximumLength(15);
            l.RuleFor(x => x.DiagnosisCode).MaximumLength(10);
            l.RuleFor(x => x.Quantity).InclusiveBetween(1, 999);
            l.RuleFor(x => x.ClaimedAmount).GreaterThan(0).PrecisionScale(18, 2, true);
        });
    }
}

/// <summary>Builds the adjudication context from the membership, the plan's static data and the accumulators (ADR-012).</summary>
public sealed class BenefitContexts(IHealthRepository health, IHealthCodes codes, IExclusionCodes exclusions, ITenantContext tenant)
{
    /// <summary>Claim history is read two years back (repeat services), or further when an edit rule's window needs it.</summary>
    private static DateOnly PriorFrom(DateOnly serviceDate, IReadOnlyList<EditRule> edits)
    {
        var from = serviceDate.AddYears(-2);
        return edits.Max(e => e.WindowDays) is { } window && serviceDate.AddDays(-window) < from ? serviceDate.AddDays(-window) : from;
    }

    /// <summary>The context for a claim's lines (or, without lines, for reserving an authorisation's categories).</summary>
    public async Task<Result<AdjudicationContext>> For(HealthMembership membership, Guid patientPartyId, string? providerId, DateOnly serviceDate, Guid claimId,
        IReadOnlyList<ClaimLineInput> lines, IReadOnlyCollection<string>? categories, bool memberClaim, IReadOnlyDictionary<int, ReviewDecision>? reviews,
        CancellationToken cancellationToken)
    {
        if (await codes.Plan(membership.PlanCode, membership.OptionCode, serviceDate, cancellationToken) is not { } plan)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{membership.PlanCode}/{membership.OptionCode} is not a plan option in force on {serviceDate:yyyy-MM-dd}.");
        }

        var rules = await codes.Rules(plan.Terms, serviceDate, cancellationToken);
        var provider = providerId is null ? null : await codes.Provider(providerId, serviceDate, cancellationToken);
        var network = await codes.Network(plan.Terms, provider, serviceDate, cancellationToken);
        var tariffCodes = lines.Select(l => l.TariffCode).Distinct().ToList();
        var schedules = new[] { network.ContractTariffSchedule, network.NetworkTariffSchedule, plan.Terms.ReferenceTariffSchedule }.OfType<string>().Distinct().ToList();
        var codeSystems = await codes.CodeSystems(tariffCodes, serviceDate, cancellationToken);
        var context = new AdjudicationContext
        {
            ClaimId = claimId, ServiceDate = serviceDate, PatientPartyId = patientPartyId, PrincipalPartyId = membership.PrincipalPartyId,
            MembershipStart = membership.StartDate, MemberClaim = memberClaim, Plan = plan.Terms, Provider = network, OutOfNetworkTreatment = rules.OutOfNetworkTreatment,
            Tariffs = await codes.Tariffs(schedules, tariffCodes, serviceDate, cancellationToken), Modifiers = rules.Modifiers, Mappings = rules.Mappings, CodeSystems = codeSystems,
            CostShares = rules.CostShares, ScheduleLimits = rules.ScheduleLimits, LimitDefinitions = rules.LimitDefinitions,
            Exclusions = (await exclusions.InForce(tenant.RequireTenantId(), null, serviceDate, cancellationToken))
                .Select(e => new ExclusionRule(e.Code, e.DiagnosisCodes, e.ProcedureCodes)).ToList(),
            WaitingPeriods = membership.WaitingPeriods, PreauthRequired = rules.PreauthRequired, Repeats = rules.Repeats, HighCost = rules.HighCost,
            Edits = rules.Edits.Select(e => (e, Expression.Parse(e.Condition))).Where(x => x.Item2.IsSuccess)
                .Select(x => new EditRule(x.e.Code, x.Item2.Value, x.e.Outcome, x.e.ReasonCode, x.e.WindowDays)).ToList(),
            Reviews = reviews ?? new Dictionary<int, ReviewDecision>(),
        };

        if (providerId is not null && lines.Count > 0)
        {
            var authorised = new List<AuthorisedLine>();
            foreach (var preauth in await health.MatchingPreauthorisations(membership.Id, patientPartyId, providerId, serviceDate, cancellationToken))
            {
                authorised.AddRange(preauth.Lines.Select(l => new AuthorisedLine(preauth.Id, l.Id, l.ProcedureCode, l.AuthorisedAmount, l.ReservedAmount, l.ConsumedAmount,
                    l.BenefitCategory)));
            }

            context = context with { Authorised = authorised, Prior = await health.PriorServices(patientPartyId, PriorFrom(serviceDate, context.Edits), claimId, cancellationToken) };
        }

        // The accumulators of every category the lines (or the authorisation) fall in.
        var wanted = (categories ?? lines.Select(l => BenefitCategories.Of(l.TariffCode, codeSystems.GetValueOrDefault(l.TariffCode), network.ProviderType, rules.Mappings))
                .OfType<string>().ToList()).Distinct().ToList();
        var keys = wanted.SelectMany(c => Limits.For(c, context, serviceDate).Accumulators.Keys).Distinct().ToList();
        var accumulators = await health.Accumulators(membership.Id, keys, cancellationToken);
        return context with { Accumulators = accumulators.ToDictionary(a => a.Key, a => a.State) };
    }

    /// <summary>Applies a claim's (or authorisation's) accumulator changes: an accumulator is opened for a period when it does not exist yet.</summary>
    public async Task<Result> Apply(Guid membershipId, IEnumerable<AccumulatorChange> changes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var list = changes.ToList();
        var existing = (await health.Accumulators(membershipId, list.Select(c => c.Key).ToList(), cancellationToken)).ToDictionary(a => a.Key);
        foreach (var change in list)
        {
            if (!existing.TryGetValue(change.Key, out var accumulator))
            {
                accumulator = BenefitAccumulator.OpenNewPeriod(Guid.CreateVersion7(), membershipId, change.Key, change.Limit);
                health.Add(accumulator);
                existing[change.Key] = accumulator;
            }

            var applied = accumulator.ConsumeBenefit(change, now);
            if (applied.IsFailure)
            {
                return applied;
            }
        }

        return Result.Success();
    }
}

internal sealed class RequestAuthorisationHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IAuditTrail audit)
    : ICommandHandler<RequestAuthorisation, PreauthorisationView>
{
    public async Task<Result<PreauthorisationView>> Handle(RequestAuthorisation command, CancellationToken cancellationToken)
    {
        // The member is eligible (HLT-005); the provider is in the register.
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership || !membership.Covers(command.PatientPartyId, businessDate.Today))
        {
            return Error.Conflict(HealthErrorCodes.MemberNotCovered, "The patient is not covered by the membership today.");
        }

        if (await codes.Provider(command.ProviderId, businessDate.Today, cancellationToken) is not { Status: "ACTIVE" })
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.ProviderId} is not an active provider.");
        }

        var id = Guid.CreateVersion7();
        var preauth = Preauthorisation.RequestAuthorisation(id, Numbers.Of("PA", id, 15), membership.Id, command.PatientPartyId, command.ProviderId, command.DiagnosisCodes,
            command.ServiceType, command.Lines);
        if (preauth.IsFailure)
        {
            return preauth.Error;
        }

        health.Add(preauth.Value);
        audit.Record(new AuditEntry("HEALTH", "RequestAuthorisation", nameof(Preauthorisation), id.ToString()));
        return PreauthorisationView.From(preauth.Value);
    }
}

internal sealed class AuthorisationHandlers(IHealthRepository health, IHealthCodes codes, BenefitContexts contexts, IExecutionContext execution, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) :
    ICommandHandler<ReviewAuthorisation, PreauthorisationView>,
    ICommandHandler<ApproveAuthorisation, PreauthorisationView>,
    ICommandHandler<RejectAuthorisation, PreauthorisationView>,
    ICommandHandler<ModifyAuthorisation, PreauthorisationView>,
    ICommandHandler<CancelAuthorisation, PreauthorisationView>,
    ICommandHandler<ConfirmConditionsMet, PreauthorisationView>
{
    public Task<Result<PreauthorisationView>> Handle(ConfirmConditionsMet command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "ConfirmConditionsMet", (p, user) => Task.FromResult(p.ConfirmConditionsMet(user)), cancellationToken);

    public Task<Result<PreauthorisationView>> Handle(ReviewAuthorisation command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "ReviewClinically", (p, user) => Task.FromResult(p.ReviewClinically(user)), cancellationToken);

    /// <summary>
    /// Command ApproveAuthorisation: each line's category, and the authorised amount at most the available benefit of
    /// that category (domain-model.json invariant); valid for the service type's days.
    /// </summary>
    public Task<Result<PreauthorisationView>> Handle(ApproveAuthorisation command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "ApproveAuthorisation", async (preauth, user) =>
        {
            if (await health.Membership(preauth.MembershipId, cancellationToken) is not { } membership)
            {
                return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
            }

            if (await codes.ValidityDays(preauth.ServiceType ?? "", businessDate.Today, cancellationToken) is not { } days)
            {
                return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{preauth.ServiceType} has no authorisation validity period in force.");
            }

            var lines = preauth.Lines.Select(l => new ClaimLineInput(0, l.ProcedureCode, null, l.Quantity, 0, [])).ToList();
            var context = await contexts.For(membership, preauth.PatientPartyId, preauth.ProviderId, command.ValidFrom, Guid.Empty, lines, null, false, null, cancellationToken);
            if (context.IsFailure)
            {
                return context.Error;
            }

            var authorisations = new List<LineAuthorisation>();
            foreach (var amount in command.Lines)
            {
                var line = preauth.Lines.SingleOrDefault(l => l.Id == amount.LineId);
                var category = line is null ? null : BenefitCategories.Of(line.ProcedureCode, context.Value.CodeSystems.GetValueOrDefault(line.ProcedureCode),
                    context.Value.Provider.ProviderType, context.Value.Mappings);
                if (category is null)
                {
                    return Error.Validation(HealthErrorCodes.CodeNotInForce, "Each authorised line is a line of the request whose code maps to a benefit category.");
                }

                authorisations.Add(new LineAuthorisation(amount.LineId, amount.AuthorisedAmount, category));
            }

            foreach (var group in authorisations.GroupBy(a => a.Category))
            {
                var available = Limits.For(group.Key, context.Value, command.ValidFrom).Accumulators
                    .Select(k => context.Value.Accumulators.TryGetValue(k.Key, out var state) ? state.Remaining : k.Value).Where(r => r is not null).Min();
                if (available is { } left && group.Sum(a => a.AuthorisedAmount) > left)
                {
                    return Error.Validation(HealthErrorCodes.AuthorisationAboveBenefit, $"{group.Key} has {left:0.00} of benefit left.");
                }
            }

            return preauth.Approve(authorisations, command.ValidFrom, days, command.Conditions, user, clock.UtcNow);
        }, cancellationToken);

    public Task<Result<PreauthorisationView>> Handle(RejectAuthorisation command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "RejectAuthorisation", (p, user) => Task.FromResult(p.RejectAuthorisation(command.Reason, user, clock.UtcNow)), cancellationToken);

    public Task<Result<PreauthorisationView>> Handle(ModifyAuthorisation command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "ModifyAuthorisation", (p, _) => Task.FromResult(p.ModifyAuthorisation(command.LineId, command.AuthorisedAmount, clock.UtcNow)),
            cancellationToken);

    public Task<Result<PreauthorisationView>> Handle(CancelAuthorisation command, CancellationToken cancellationToken) =>
        Apply(command.PreauthorisationId, "CancelAuthorisation", (p, _) => Task.FromResult(p.CancelAuthorisation(clock.UtcNow)), cancellationToken);

    private async Task<Result<PreauthorisationView>> Apply(Guid id, string operation, Func<Preauthorisation, Guid, Task<Result>> change, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.Preauthorisation(id, cancellationToken) is not { } preauth)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The authorisation was not found.");
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(Preauthorisation), preauth.Id.ToString()));
        var result = await change(preauth, user.Value);
        return result.IsFailure ? result.Error : PreauthorisationView.From(preauth);
    }
}

internal sealed class GetAuthorisationHandler(IHealthRepository health) : IQueryHandler<GetAuthorisation, PreauthorisationView>
{
    public async Task<Result<PreauthorisationView>> Handle(GetAuthorisation query, CancellationToken cancellationToken) =>
        await health.Preauthorisation(query.PreauthorisationId, cancellationToken) is { } preauth
            ? PreauthorisationView.From(preauth)
            : Error.NotFound(ErrorCodes.NotFound, "The authorisation was not found.");
}

/// <summary>
/// Command SubmitClaim (HLT-006). The fraud indicators in force are evaluated on the claim's facts; when the fired
/// scores reach an indicator's threshold, a fraud case opens (as the Claims Framework reads them). An indicator that
/// cannot be evaluated is logged and skipped.
/// </summary>
internal sealed class SubmitMedicalClaimHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit, ILogger<SubmitMedicalClaimHandler> logger)
    : ICommandHandler<SubmitMedicalClaim, MedicalClaimView>
{
    public async Task<Result<MedicalClaimView>> Handle(SubmitMedicalClaim command, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var claim = MedicalClaim.SubmitClaim(id, Numbers.Of("MC", id, 25), command.Source, command.ProviderId, command.MembershipId, command.PatientPartyId,
            command.ServiceDate, command.FormatCode, command.Currency, command.PreauthorisationId, command.Lines, clock.UtcNow);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        health.Add(claim.Value);
        audit.Record(new AuditEntry("HEALTH", "SubmitClaim", nameof(MedicalClaim), id.ToString()));

        var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["total_claimed"] = claim.Value.TotalClaimed, ["line_count"] = (decimal)claim.Value.Lines.Count, ["source"] = claim.Value.Source,
            ["provider_id"] = claim.Value.ProviderId, ["days_to_submission"] = (decimal)(businessDate.Today.DayNumber - command.ServiceDate.DayNumber),
        };
        var fired = new List<FraudIndicator>();
        foreach (var indicator in await codes.FraudIndicators(businessDate.Today, cancellationToken))
        {
            var logic = Expression.Parse(indicator.Logic);
            var hit = logic.IsSuccess && logic.Value.Variables.All(facts.ContainsKey) ? logic.Value.Evaluate(facts, today: businessDate.Today) : null;
            if (hit is null || hit.IsFailure)
            {
                logger.LogWarning("Fraud indicator {Indicator} could not be evaluated on claim {ClaimId}", indicator.Code, id);
            }
            else if (hit.Value)
            {
                fired.Add(indicator);
            }
        }

        var total = fired.Sum(f => f.Score);
        if (fired.Any(f => total >= f.Threshold))
        {
            health.Add(HealthFraudCase.OpenCase(Guid.CreateVersion7(), "CLAIM", claim.Value.ClaimNumber, fired.Select(f => f.Code).ToList(), clock.UtcNow));
        }

        return MedicalClaimView.From(claim.Value);
    }
}

/// <summary>Command ValidateClaim (HLT-007): the membership covers the patient on the service date, the format, provider and codes are known.</summary>
internal sealed class ValidateMedicalClaimHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<ValidateMedicalClaim, MedicalClaimView>
{
    public async Task<Result<MedicalClaimView>> Handle(ValidateMedicalClaim command, CancellationToken cancellationToken)
    {
        if (await health.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        var day = claim.ServiceDate;
        var membership = await health.Membership(claim.MembershipId, cancellationToken);
        var codesUsed = claim.Lines.Select(l => l.TariffCode).Distinct().ToList();
        var diagnoses = claim.Lines.Select(l => l.DiagnosisCode).OfType<string>().Distinct().ToList();
        string? problem = null;
        if (membership is null || !membership.Covers(claim.PatientPartyId, day))
        {
            problem = "NOT_COVERED";
        }
        else if (!await codes.ClaimFormatExists(claim.FormatCode, businessDate.Today, cancellationToken))
        {
            problem = "UNKNOWN_FORMAT";
        }
        else if (claim.ProviderId is { } providerId && await codes.Provider(providerId, day, cancellationToken) is not { Status: "ACTIVE" })
        {
            problem = "PROVIDER_NOT_ACTIVE";
        }
        else if ((await codes.CodeSystems(codesUsed, day, cancellationToken)).Count != codesUsed.Count)
        {
            problem = "UNKNOWN_TARIFF_CODE";
        }
        else if ((await codes.KnownDiagnoses(diagnoses, day, cancellationToken)).Count != diagnoses.Count)
        {
            problem = "UNKNOWN_DIAGNOSIS";
        }

        var result = claim.ValidateClaim(problem, clock.UtcNow);
        return result.IsFailure ? result.Error : MedicalClaimView.From(claim);
    }
}

/// <summary>
/// Command AdjudicateClaim (and "Information received"): the engine's result is applied to the claim and, unless a
/// line pends, to the accumulators it consumes, in one transaction with optimistic locking (ADR-012).
/// </summary>
internal sealed class AdjudicateMedicalClaimHandler(IHealthRepository health, BenefitContexts contexts, IClock clock, IAuditTrail audit) :
    ICommandHandler<AdjudicateMedicalClaim, MedicalClaimView>,
    ICommandHandler<ReviewPendedClaim, MedicalClaimView>
{
    public Task<Result<MedicalClaimView>> Handle(AdjudicateMedicalClaim command, CancellationToken cancellationToken) =>
        Adjudicate(command.ClaimId, null, cancellationToken);

    public Task<Result<MedicalClaimView>> Handle(ReviewPendedClaim command, CancellationToken cancellationToken) =>
        Adjudicate(command.ClaimId, command.Reviews.ToDictionary(r => r.LineNo, r => r.Decision), cancellationToken);

    private async Task<Result<MedicalClaimView>> Adjudicate(Guid claimId, IReadOnlyDictionary<int, ReviewDecision>? reviews, CancellationToken cancellationToken)
    {
        if (await health.Claim(claimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        if (claim.Status is not (MedicalClaimStatus.Validated or MedicalClaimStatus.Pended) || (reviews is not null) != (claim.Status == MedicalClaimStatus.Pended))
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"Claim {claim.ClaimNumber} is {claim.Status}.");
        }

        // The membership, then its accumulators by id, are locked for this transaction: concurrent claims on the same
        // benefits (a family pool, say) wait in one order, so they never deadlock and never read a stale balance (ADR-012).
        await health.LockBenefits(claim.MembershipId, cancellationToken);
        if (await health.Membership(claim.MembershipId, cancellationToken) is not { } membership)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        var lines = claim.Lines.Select(l => l.ToInput()).ToList();
        var context = await contexts.For(membership, claim.PatientPartyId, claim.ProviderId, claim.ServiceDate, claim.Id, lines, null, claim.Source == ClaimSources.Member,
            reviews, cancellationToken);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var result = AdjudicationEngine.Adjudicate(context.Value, lines);
        if (!result.Pended)
        {
            var applied = await contexts.Apply(membership.Id, result.Changes, clock.UtcNow, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        audit.Record(new AuditEntry("HEALTH", reviews is null ? "AdjudicateClaim" : "ReviewPendedClaim", nameof(MedicalClaim), claim.Id.ToString()));
        var adjudicated = claim.AdjudicateClaim(result, clock.UtcNow);
        return adjudicated.IsFailure ? adjudicated.Error : MedicalClaimView.From(claim);
    }
}

internal sealed class MedicalClaimDecisions(IHealthRepository health, IExecutionContext execution, IClock clock, IAuditTrail audit) :
    ICommandHandler<ApproveMedicalClaim, MedicalClaimView>,
    ICommandHandler<RejectMedicalClaim, MedicalClaimView>,
    ICommandHandler<ReverseMedicalClaim, MedicalClaimView>
{
    public Task<Result<MedicalClaimView>> Handle(ApproveMedicalClaim command, CancellationToken cancellationToken) =>
        Apply(command.ClaimId, "ApproveForPayment", (c, user) => Task.FromResult(c.ApproveForPayment(user, clock.UtcNow)), cancellationToken);

    public Task<Result<MedicalClaimView>> Handle(RejectMedicalClaim command, CancellationToken cancellationToken) =>
        Apply(command.ClaimId, "RejectClaim", (c, _) => Task.FromResult(c.RejectClaim(command.Reason, clock.UtcNow)), cancellationToken);

    /// <summary>Command ReverseClaim: the claim's consumption is given back on the accumulators covering its service date (same transaction, ADR-012).</summary>
    public Task<Result<MedicalClaimView>> Handle(ReverseMedicalClaim command, CancellationToken cancellationToken) =>
        Apply(command.ClaimId, "ReverseClaim", async (claim, _) =>
        {
            await health.LockBenefits(claim.MembershipId, cancellationToken);
            var reversed = claim.ReverseClaim(command.Reason, clock.UtcNow);
            if (reversed.IsFailure)
            {
                return reversed;
            }

            foreach (var line in claim.Lines.Where(l => l.PaidAmount > 0 && l.BenefitCategory is not null))
            {
                foreach (var accumulator in await health.AccumulatorsCovering(claim.MembershipId, line.BenefitCategory!, claim.ServiceDate, cancellationToken))
                {
                    accumulator.ReverseConsumption(line.PaidAmount);
                }
            }

            return Result.Success();
        }, cancellationToken);

    private async Task<Result<MedicalClaimView>> Apply(Guid id, string operation, Func<MedicalClaim, Guid, Task<Result>> change, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.Claim(id, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        audit.Record(new AuditEntry("HEALTH", operation, nameof(MedicalClaim), claim.Id.ToString()));
        var result = await change(claim, user.Value);
        return result.IsFailure ? result.Error : MedicalClaimView.From(claim);
    }
}

internal sealed class GetMedicalClaimHandler(IHealthRepository health) : IQueryHandler<GetMedicalClaim, MedicalClaimView>
{
    public async Task<Result<MedicalClaimView>> Handle(GetMedicalClaim query, CancellationToken cancellationToken) =>
        await health.Claim(query.ClaimId, cancellationToken) is { } claim ? MedicalClaimView.From(claim) : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
}
