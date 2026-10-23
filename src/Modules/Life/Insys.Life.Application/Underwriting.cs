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
using Insys.Life.Domain;
using Insys.Identity.Contracts;
using Insys.Life.Domain.Underwriting;
using Insys.Rules.Contracts;

namespace Insys.Life.Application;

public sealed record GetUnderwritingCase(Guid CaseId) : IQuery<CaseView>;

public sealed record GetProposalCase(Guid ProposalId) : IQuery<CaseView>;

public sealed record RequestEvidence(Guid CaseId, string EvidenceType, bool Mandatory) : ICommand<CaseView>;

public sealed record RecordEvidence(Guid CaseId, Guid RequirementId, Guid DocumentId) : ICommand<CaseView>;

public sealed record WaiveEvidence(Guid CaseId, Guid RequirementId, string Reason) : ICommand<CaseView>;

public enum CaseAction
{
    StartAssessment,
    AnswerReferral,
}

public sealed record ChangeCase(Guid CaseId, CaseAction Action) : ICommand<CaseView>;

public sealed record ReferUnderwritingCase(Guid CaseId, string ToRole) : ICommand<CaseView>;

/// <summary>Command DecideCase, as the maker: the outcome, the decision code and reason, and the terms per benefit. A second person approves it in Workflow.</summary>
public sealed record DecideUnderwritingCase(Guid CaseId, UnderwritingOutcome Outcome, string DecisionCode, string Reason, IReadOnlyList<DecisionItemRequest>? Items)
    : ICommand<CaseView>;

public sealed class RequestEvidenceValidator : AbstractValidator<RequestEvidence>
{
    public RequestEvidenceValidator() => RuleFor(c => c.EvidenceType).NotEmpty().MaximumLength(20);
}

public sealed class DecideUnderwritingCaseValidator : AbstractValidator<DecideUnderwritingCase>
{
    public DecideUnderwritingCaseValidator()
    {
        RuleFor(c => c.DecisionCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(20);
        RuleForEach(c => c.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.BenefitCode).NotEmpty().MaximumLength(20);
            i.RuleFor(x => x.DecisionCode).NotEmpty().MaximumLength(15);
            i.RuleFor(x => x.LoadingPct).PrecisionScale(7, 2, true);
            i.RuleFor(x => x.LoadingPerMille).PrecisionScale(9, 4, true);
            i.RuleFor(x => x.ExclusionCode).MaximumLength(20);
        });
    }
}

internal static class Cases
{
    public static async Task<Result<LifeUnderwritingCase>> Load(ILifeRepository life, Guid id, CancellationToken cancellationToken) =>
        await life.Case(id, cancellationToken) is { } found ? found : Error.NotFound(ErrorCodes.NotFound, "The underwriting case was not found.");
}

internal sealed class GetUnderwritingCaseHandler(ILifeRepository life) : IQueryHandler<GetUnderwritingCase, CaseView>
{
    public async Task<Result<CaseView>> Handle(GetUnderwritingCase query, CancellationToken cancellationToken)
    {
        var found = await Cases.Load(life, query.CaseId, cancellationToken);
        return found.IsFailure ? found.Error : CaseView.From(found.Value);
    }
}

internal sealed class GetProposalCaseHandler(ILifeRepository life) : IQueryHandler<GetProposalCase, CaseView>
{
    public async Task<Result<CaseView>> Handle(GetProposalCase query, CancellationToken cancellationToken) =>
        await life.CaseForProposal(query.ProposalId, cancellationToken) is { } found
            ? CaseView.From(found)
            : Error.NotFound(ErrorCodes.NotFound, "The proposal has no underwriting case.");
}

internal sealed class RequestEvidenceHandler(ILifeRepository life, IClock clock) : ICommandHandler<RequestEvidence, CaseView>
{
    public async Task<Result<CaseView>> Handle(RequestEvidence command, CancellationToken cancellationToken)
    {
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var result = found.Value.RequestEvidence(command.EvidenceType, command.Mandatory, clock.UtcNow);
        return result.IsFailure ? result.Error : CaseView.From(found.Value);
    }
}

internal sealed class RecordEvidenceHandler(ILifeRepository life, IClock clock) : ICommandHandler<RecordEvidence, CaseView>
{
    public async Task<Result<CaseView>> Handle(RecordEvidence command, CancellationToken cancellationToken)
    {
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var result = found.Value.RecordEvidence(command.RequirementId, command.DocumentId, clock.UtcNow);
        return result.IsFailure ? result.Error : CaseView.From(found.Value);
    }
}

internal sealed class WaiveEvidenceHandler(ILifeRepository life, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<WaiveEvidence, CaseView>
{
    public async Task<Result<CaseView>> Handle(WaiveEvidence command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (user.IsFailure || found.IsFailure)
        {
            return user.IsFailure ? user.Error : found.Error!;
        }

        audit.Record(new AuditEntry("LIFE", "WaiveEvidence", nameof(LifeUnderwritingCase), found.Value.Id.ToString(),
            new Dictionary<string, string?> { ["requirement"] = command.RequirementId.ToString() }));
        var result = found.Value.WaiveEvidence(command.RequirementId, command.Reason, user.Value);
        return result.IsFailure ? result.Error : CaseView.From(found.Value);
    }
}

internal sealed class ChangeCaseHandler(ILifeRepository life) : ICommandHandler<ChangeCase, CaseView>
{
    public async Task<Result<CaseView>> Handle(ChangeCase command, CancellationToken cancellationToken)
    {
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var result = command.Action == CaseAction.StartAssessment ? found.Value.StartAssessment() : found.Value.AnswerReferral();
        return result.IsFailure ? result.Error : CaseView.From(found.Value);
    }
}

internal sealed class ReferUnderwritingCaseHandler(ILifeRepository life) : ICommandHandler<ReferUnderwritingCase, CaseView>
{
    public async Task<Result<CaseView>> Handle(ReferUnderwritingCase command, CancellationToken cancellationToken)
    {
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var result = found.Value.ReferCase(command.ToRole);
        return result.IsFailure ? result.Error : CaseView.From(found.Value);
    }
}

/// <summary>
/// Command DecideCase. Single-person when the decision matches the underwriting rule outcome and the underwriter's
/// roles hold authority for the product, the total sum assured and every decision code (rules.cfg_authority_matrices);
/// otherwise it is requested in Workflow for a second person (an override, or above authority). Audited either way.
/// </summary>
internal sealed class DecideUnderwritingCaseHandler(ILifeRepository life, ILifeCodes codes, IUserRoleQuery roles, IUnderwritingAuthorities authorities, ITenantContext tenant,
    IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<DecideUnderwritingCase, CaseView>
{
    public async Task<Result<CaseView>> Handle(DecideUnderwritingCase command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var found = await Cases.Load(life, command.CaseId, cancellationToken);
        if (user.IsFailure || found.IsFailure)
        {
            return user.IsFailure ? user.Error : found.Error!;
        }

        var underwritingCase = found.Value;
        if (await life.Proposal(underwritingCase.ProposalId, cancellationToken) is not { } proposal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
        }

        var items = command.Items ?? [];
        var decisionCodes = await codes.DecisionCodes(clock.UtcToday, cancellationToken);
        if (items.Select(i => i.DecisionCode).Append(command.DecisionCode).FirstOrDefault(c => !decisionCodes.ContainsKey(c)) is { } unknown)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{unknown} is not an underwriting decision code in force.");
        }

        if (items.FirstOrDefault(i => proposal.Covers.All(c => c.BenefitCode != i.BenefitCode)) is { } stray)
        {
            return Error.Validation(LifeErrorCodes.BenefitNotInProduct, $"{stray.BenefitCode} is not a cover on the proposal.");
        }

        var codesRequiringTerms = decisionCodes.Where(c => c.Value).Select(c => c.Key).ToHashSet();
        var tenantId = tenant.RequireTenantId();
        var userRoles = await roles.EffectiveRoles(tenantId, user.Value, cancellationToken);
        var codesUsed = items.Select(i => i.DecisionCode).Append(command.DecisionCode).Distinct().ToList();
        var authority = (await authorities.For(tenantId, userRoles, proposal.ProductCode, clock.UtcToday, cancellationToken))
            .FirstOrDefault(a => a.MaxSumAssured >= proposal.TotalSumAssured && codesUsed.All(a.DecisionTypes.Contains));
        var single = authority is not null && underwritingCase.MatchesRuleOutcome(command.Outcome);
        audit.Record(new AuditEntry("LIFE", "DecideCase", nameof(LifeUnderwritingCase), underwritingCase.Id.ToString(), new Dictionary<string, string?>
        {
            ["outcome"] = command.Outcome.ToString(), ["decision"] = command.DecisionCode, ["ruleOutcome"] = underwritingCase.RuleOutcome,
            ["authority"] = authority?.RoleCode, ["secondPerson"] = (!single).ToString(),
        }));
        var result = single
            ? underwritingCase.DecideCase(command.Outcome, command.DecisionCode, command.Reason, items, codesRequiringTerms, user.Value, authority!.RoleCode, clock.UtcNow)
            : underwritingCase.RequestDecision(command.Outcome, command.DecisionCode, command.Reason, items, codesRequiringTerms, proposal.TotalSumAssured,
                proposal.Currency, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : CaseView.From(underwritingCase);
    }
}
