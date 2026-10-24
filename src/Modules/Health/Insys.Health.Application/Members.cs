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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Health.Domain;
using Insys.Health.Domain.Adjudication;
using Insys.Health.Domain.Benefits;
using Insys.Health.Domain.Members;
using Insys.Party.Contracts;
using Insys.Tenancy.Contracts;

namespace Insys.Health.Application;

public sealed record RegisterHealthScheme(string SponsorCode, DateOnly StartDate, DateOnly RenewalDate, string Currency) : ICommand<SchemeView>;

public enum SchemeAction
{
    Renew,
    Terminate,
}

public sealed record ChangeHealthScheme(Guid SchemeId, SchemeAction Action) : ICommand<SchemeView>;

public sealed record GetHealthScheme(Guid SchemeId) : IQuery<SchemeView>;

/// <param name="Facts">The member's facts the scheme category's eligibility rule reads, as a JSON object.</param>
/// <param name="OrgNodeCode">
/// The organisation node (normally a branch) that owns the membership and its claims (F103); when omitted, the caller's one
/// node, else the tenant's root legal entity.
/// </param>
public sealed record EnrolHealthMember(Guid PrincipalPartyId, Guid? SchemeId, string? MemberCategory, string PlanCode, string OptionCode, DateOnly StartDate, string Currency,
    Guid? PayerPartyId, Guid? BillingAccountId, string CollectionMethod, JsonElement? Facts, string? OrgNodeCode = null) : ICommand<MembershipView>;

public sealed record AddHealthDependant(Guid MembershipId, Guid PartyId, string DependantType, bool Student, DateOnly StartDate) : ICommand<MembershipView>;

public sealed record RemoveHealthDependant(Guid MembershipId, Guid DependantId, DateOnly LastDay) : ICommand<MembershipView>;

public sealed record ChangeHealthPlan(Guid MembershipId, string PlanCode, string OptionCode, DateOnly EffectiveDate, string Reason) : ICommand<MembershipView>;

public enum MembershipAction
{
    Suspend,
    Reinstate,
    Terminate,
}

public sealed record ChangeMembershipStatus(Guid MembershipId, MembershipAction Action, DateOnly EffectiveDate, string Reason) : ICommand<MembershipView>;

public sealed record GetMembership(Guid MembershipId) : IQuery<MembershipView>;

public sealed record GetAccumulators(Guid MembershipId) : IQuery<IReadOnlyList<AccumulatorView>>;

/// <summary>Command CheckEligibility (HLT-003): is the member (by number and suffix) covered for a service on a day.</summary>
public sealed record CheckEligibility(string MemberNumber, string DependantSuffix, string? ProviderId, string ServiceType, DateOnly? ServiceDate) : ICommand<EligibilityView>;

public sealed class EnrolHealthMemberValidator : AbstractValidator<EnrolHealthMember>
{
    public EnrolHealthMemberValidator()
    {
        RuleFor(c => c.PrincipalPartyId).NotEmpty();
        RuleFor(c => c.MemberCategory).MaximumLength(10);
        RuleFor(c => c.PlanCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.OptionCode).NotEmpty().MaximumLength(10);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.CollectionMethod).NotEmpty().MaximumLength(15);
        RuleFor(c => c.MemberCategory).NotEmpty().When(c => c.SchemeId is not null).WithMessage("A scheme member has a member category.");
    }
}

public sealed class CheckEligibilityValidator : AbstractValidator<CheckEligibility>
{
    public CheckEligibilityValidator()
    {
        RuleFor(c => c.MemberNumber).NotEmpty().MaximumLength(12);
        RuleFor(c => c.DependantSuffix).NotEmpty().Length(2);
        RuleFor(c => c.ServiceType).NotEmpty().MaximumLength(20);
        RuleFor(c => c.ProviderId).MaximumLength(15);
    }
}

internal sealed class RegisterHealthSchemeHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IAuditTrail audit) : ICommandHandler<RegisterHealthScheme, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(RegisterHealthScheme command, CancellationToken cancellationToken)
    {
        if (!await codes.SponsorExists(command.SponsorCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.SponsorCode} is not a sponsor in the register.");
        }

        var id = Guid.CreateVersion7();
        var scheme = HealthScheme.RegisterScheme(id, Numbers.Of("HS", id, 12), command.SponsorCode, command.StartDate, command.RenewalDate, command.Currency, businessDate.Today);
        if (scheme.IsFailure)
        {
            return scheme.Error;
        }

        health.Add(scheme.Value);
        audit.Record(new AuditEntry("HEALTH", "RegisterScheme", nameof(HealthScheme), id.ToString()));
        return SchemeView.From(scheme.Value);
    }
}

internal sealed class ChangeHealthSchemeHandler(IHealthRepository health, IClock clock) : ICommandHandler<ChangeHealthScheme, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(ChangeHealthScheme command, CancellationToken cancellationToken)
    {
        if (await health.Scheme(command.SchemeId, cancellationToken) is not { } scheme)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
        }

        var result = command.Action == SchemeAction.Renew ? scheme.RenewScheme(clock.UtcNow) : scheme.TerminateScheme();
        return result.IsFailure ? result.Error : SchemeView.From(scheme);
    }
}

internal sealed class GetHealthSchemeHandler(IHealthRepository health) : IQueryHandler<GetHealthScheme, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(GetHealthScheme query, CancellationToken cancellationToken) =>
        await health.Scheme(query.SchemeId, cancellationToken) is { } scheme ? SchemeView.From(scheme) : Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
}

internal sealed class EnrolHealthMemberHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit,
    ITenantContext tenant, IExecutionContext execution, IOrganisationStructure organisation) : ICommandHandler<EnrolHealthMember, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(EnrolHealthMember command, CancellationToken cancellationToken)
    {
        var day = businessDate.Today;

        // F103 (addenda/access-scopes.md §2): the owning node. Health records have no product to scope by (OQ-080).
        var nodes = await organisation.Nodes(tenant.RequireTenantId(), day, cancellationToken);
        var node = execution.PlaceNew(null, command.OrgNodeCode, nodes.Select(n => n.NodeCode).ToList(), OrganisationNodes.Root(nodes));
        if (node.IsFailure)
        {
            return node.Error;
        }

        if (await codes.Plan(command.PlanCode, command.OptionCode, day, cancellationToken) is not { } plan)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.PlanCode}/{command.OptionCode} is not a plan option in force.");
        }

        ContributionRate? rate = null;
        if (command.SchemeId is { } schemeId)
        {
            if (await health.Scheme(schemeId, cancellationToken) is not { Status: not SchemeStatus.Terminated } scheme)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, "The scheme is not open for enrolment.");
            }

            // Every member category maps to a plan; the member is enrolled on that plan, if the category's rule admits them.
            if (await codes.SchemeCategory(command.MemberCategory!, day, cancellationToken) is not { } category || category.Plan != command.PlanCode)
            {
                return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.MemberCategory} is not a member category on plan {command.PlanCode}.");
            }

            var eligible = Rules.Evaluate(category.EligibilityRule, command.Facts, day);
            if (eligible.IsFailure)
            {
                return eligible.Error;
            }

            var contribution = await Contributions.Rate(codes, scheme, category.CategoryCode, day, cancellationToken);
            if (contribution.IsFailure)
            {
                return contribution.Error;
            }

            rate = contribution.Value;
        }

        // Waiting periods in force now are fixed for the membership (domain-model.json invariant).
        var waiting = new List<WaitingPeriod>();
        foreach (var rule in await codes.WaitingPeriodRules(day, cancellationToken))
        {
            if (IsoDuration.Add(command.StartDate, rule.Duration) is { } ends)
            {
                waiting.Add(new WaitingPeriod(rule.RuleCode, rule.Type, ends, rule.Exceptions, rule.DiagnosisCodes, rule.ProcedureCodes));
            }
        }

        if (await codes.MemberNumberPrefix(day, cancellationToken) is not { } prefix)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, "No member numbering scheme is in force.");
        }

        var id = Guid.CreateVersion7();
        var membership = HealthMembership.EnrolMember(id, Numbers.Of(prefix, id, 12), command.PrincipalPartyId, command.SchemeId, command.MemberCategory,
            new PlanOption(plan.Terms.PlanCode, plan.Terms.OptionCode, plan.Version), command.StartDate, waiting,
            new ContributionBilling(command.PayerPartyId ?? command.PrincipalPartyId, command.BillingAccountId ?? Guid.CreateVersion7(), command.CollectionMethod, rate), command.Currency, day,
            clock.UtcNow, node.Value);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        health.Add(membership.Value);
        audit.Record(new AuditEntry("HEALTH", "EnrolMember", nameof(HealthMembership), id.ToString()));
        return MembershipView.From(membership.Value);
    }
}

internal static class Rules
{
    /// <summary>An eligibility rule (an expression) over the facts given; a rule that cannot be evaluated refuses the enrolment.</summary>
    public static Result Evaluate(string rule, JsonElement? facts, DateOnly day)
    {
        var expression = Expression.Parse(rule);
        if (expression.IsFailure)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"The eligibility rule cannot be evaluated: {expression.Error.Message}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (facts is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var property in json.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }

        var result = expression.Value.Evaluate(values, today: day);
        return result.IsSuccess && result.Value
            ? Result.Success()
            : Error.Validation(HealthErrorCodes.DependantNotEligible, result.IsFailure ? $"The eligibility rule could not be evaluated: {result.Error.Message}" : "The member is not eligible for the category.");
    }
}

internal sealed class AddHealthDependantHandler(IHealthRepository health, IHealthCodes codes, IPersonFacts persons, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<AddHealthDependant, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(AddHealthDependant command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        if (await codes.DependantType(command.DependantType, businessDate.Today, cancellationToken) is not { } rule)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.DependantType} is not a dependant type in force.");
        }

        if (await persons.BirthDate(tenant.RequireTenantId(), command.PartyId, cancellationToken) is not { } birthDate)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The dependant is not a person with a date of birth.");
        }

        var rate = await Contributions.Rate(health, codes, membership, businessDate.Today, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "AddDependant", nameof(HealthMembership), membership.Id.ToString()));
        var result = membership.AddDependant(command.PartyId, rule, birthDate, command.Student, command.StartDate, rate.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : MembershipView.From(membership);
    }
}

internal static class Contributions
{
    /// <summary>A scheme member's contribution rate; none for other members, or when the category has no contribution rule.</summary>
    public static async Task<Result<ContributionRate?>> Rate(IHealthRepository health, IHealthCodes codes, HealthMembership membership, DateOnly day,
        CancellationToken cancellationToken) =>
        membership.SchemeId is { } schemeId && membership.MemberCategory is { } category && await health.Scheme(schemeId, cancellationToken) is { } scheme
            ? await Rate(codes, scheme, category, day, cancellationToken)
            : Result.Success<ContributionRate?>(null);

    /// <summary>The employer's share is billed to the scheme's sponsor, so a share needs the sponsor's party.</summary>
    public static async Task<Result<ContributionRate?>> Rate(IHealthCodes codes, HealthScheme scheme, string category, DateOnly day, CancellationToken cancellationToken)
    {
        if (await codes.Contribution(scheme.SchemeNumber, category, day, cancellationToken) is not { } rule)
        {
            return Result.Success<ContributionRate?>(null);
        }

        var sponsor = rule.EmployerSharePct > 0 ? await codes.SponsorParty(scheme.SponsorCode, day, cancellationToken) : null;
        return rule.EmployerSharePct > 0 && sponsor is null
            ? Error.Validation(HealthErrorCodes.CodeNotInForce, $"Sponsor {scheme.SponsorCode} has no party to bill the employer's share to.")
            : new ContributionRate(rule.Rate, rule.EmployerSharePct, sponsor);
    }
}

internal sealed class RemoveHealthDependantHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<RemoveHealthDependant, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(RemoveHealthDependant command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        var rate = await Contributions.Rate(health, codes, membership, businessDate.Today, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error;
        }

        var result = membership.RemoveDependant(command.DependantId, command.LastDay, rate.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : MembershipView.From(membership);
    }
}

internal sealed class ChangeHealthPlanHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IAuditTrail audit) : ICommandHandler<ChangeHealthPlan, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(ChangeHealthPlan command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        if (await codes.Plan(command.PlanCode, command.OptionCode, businessDate.Today, cancellationToken) is not { } plan)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"{command.PlanCode}/{command.OptionCode} is not a plan option in force.");
        }

        audit.Record(new AuditEntry("HEALTH", "ChangePlan", nameof(HealthMembership), membership.Id.ToString()));
        var result = membership.ChangePlan(new PlanOption(plan.Terms.PlanCode, plan.Terms.OptionCode, plan.Version), command.EffectiveDate, command.Reason);
        return result.IsFailure ? result.Error : MembershipView.From(membership);
    }
}

internal sealed class ChangeMembershipStatusHandler(IHealthRepository health, IClock clock, IAuditTrail audit) : ICommandHandler<ChangeMembershipStatus, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(ChangeMembershipStatus command, CancellationToken cancellationToken)
    {
        if (await health.Membership(command.MembershipId, cancellationToken) is not { } membership)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
        }

        audit.Record(new AuditEntry("HEALTH", $"{command.Action}Membership", nameof(HealthMembership), membership.Id.ToString()));
        var result = command.Action switch
        {
            MembershipAction.Suspend => membership.SuspendMembership(command.EffectiveDate, command.Reason, clock.UtcNow),
            MembershipAction.Reinstate => membership.ReinstateMembership(command.EffectiveDate, command.Reason, clock.UtcNow),
            _ => membership.TerminateMembership(command.EffectiveDate, command.Reason, clock.UtcNow),
        };
        return result.IsFailure ? result.Error : MembershipView.From(membership);
    }
}

internal sealed class GetMembershipHandler(IHealthRepository health) : IQueryHandler<GetMembership, MembershipView>
{
    public async Task<Result<MembershipView>> Handle(GetMembership query, CancellationToken cancellationToken) =>
        await health.Membership(query.MembershipId, cancellationToken) is { } membership
            ? MembershipView.From(membership)
            : Error.NotFound(ErrorCodes.NotFound, "The membership was not found.");
}

internal sealed class GetAccumulatorsHandler(IHealthRepository health) : IQueryHandler<GetAccumulators, IReadOnlyList<AccumulatorView>>
{
    public async Task<Result<IReadOnlyList<AccumulatorView>>> Handle(GetAccumulators query, CancellationToken cancellationToken) =>
        (await health.AccumulatorsOf(query.MembershipId, cancellationToken)).OrderBy(a => a.BenefitCategory).ThenBy(a => a.Scope).Select(AccumulatorView.From).ToList();
}

/// <summary>
/// Command CheckEligibility (HLT-003): the outcome is ELIGIBLE when the membership covers the person on the day,
/// WAITING_PERIOD when it does but a general waiting period has not ended, NOT_ELIGIBLE when it does not cover them,
/// and NOT_FOUND when there is no such member. The answer is the tenant's response code configured for that outcome
/// (health.ref_eligibility_response_codes.outcome); an outcome without one is refused (Phase 5 review, item 5).
/// </summary>
internal sealed class CheckEligibilityHandler(IHealthRepository health, IHealthCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<CheckEligibility, EligibilityView>
{
    public async Task<Result<EligibilityView>> Handle(CheckEligibility command, CancellationToken cancellationToken)
    {
        var day = command.ServiceDate ?? businessDate.Today;
        var membership = await health.MembershipByNumber(command.MemberNumber, cancellationToken);
        var outcome = membership is null || membership.PartyFor(command.DependantSuffix) is not { } party ? "NOT_FOUND"
            : !membership.Covers(party, day) ? "NOT_ELIGIBLE"
            : membership.WaitingPeriods.Any(w => w.Type == "GENERAL" && w.EndsOn > day) ? "WAITING_PERIOD"
            : "ELIGIBLE";
        if (await codes.EligibilityResponseCode(outcome, businessDate.Today, cancellationToken) is not { } code)
        {
            return Error.Validation(HealthErrorCodes.CodeNotInForce, $"No eligibility response code is configured for the outcome {outcome}.");
        }

        var decision = EligibilityDecision.Record(Guid.CreateVersion7(), command.MemberNumber, command.DependantSuffix, command.ProviderId, command.ServiceType, code,
            clock.UtcNow);
        health.Add(decision);
        audit.Record(new AuditEntry("HEALTH", "CheckEligibility", nameof(EligibilityDecision), decision.Id.ToString()));
        return new EligibilityView(decision.Id, decision.MemberNumber, decision.DependantSuffix, decision.ProviderId, decision.ServiceType, decision.ResponseCode, outcome,
            decision.DecidedAt);
    }
}
