/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
using Insys.General.Domain;
using Insys.General.Domain.Policies;
using Insys.General.Domain.Underwriting;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

/// <summary>
/// Command IssuePolicy from the accepted quote version. The policy is billed on <paramref name="BillingAccountId"/>
/// (an existing account of the payer) or on a new account Billing opens; the payer is the policyholder unless given.
/// </summary>
public sealed record IssuePolicy(Guid QuoteId, Guid? BillingAccountId, Guid? PayerPartyId, [property: CodeList(CodeLists.BillingFrequencies, "code")] string BillingFrequency,
    [property: CodeList(CodeLists.CollectionMethods, "method_code")] string CollectionMethod, Guid? RenewalOfPolicyId)
    : ICommand<PolicyView>;

/// <summary>Command RenewPolicy, where the product's renewal rule does not re-rate; a re-rated renewal is a new quote issued with <c>renewalOfPolicyId</c>.</summary>
public sealed record RenewPolicy(Guid PolicyId) : ICommand<PolicyView>;

public sealed record CancelPolicy(Guid PolicyId, [property: CodeList(CodeLists.CancellationReasons, "reason_code")] string ReasonCode, DateOnly EffectiveDate) : ICommand<PolicyView>;

public sealed record ReinstatePolicy(Guid PolicyId) : ICommand<PolicyView>;

public sealed record GetPolicy(Guid PolicyId) : IQuery<PolicyView>;

/// <summary>Command DraftEndorsement: changes effective from a date within the policy period (GEN-003).</summary>
/// <param name="IntermediaryCode">B-001: for an INTERMEDIARY endorsement (a broker of record change), the intermediary the policy moves to; it has no changes.</param>
public sealed record DraftEndorsement(Guid PolicyId, [property: CodeList(CodeLists.EndorsementTypes, "code")] string EndorsementType, DateOnly EffectiveDate,
    IReadOnlyList<EndorsementChange> Changes, string? IntermediaryCode = null) : ICommand<EndorsementView>;

/// <summary>B-001, Wave 4 addition 3: the policyholder's broker appointment letter, a document filed against the endorsement, before it is submitted.</summary>
public sealed record RecordAppointmentLetter(Guid EndorsementId, Guid DocumentId) : ICommand<EndorsementView>;

public sealed record SubmitEndorsement(Guid EndorsementId) : ICommand<EndorsementView>;

public sealed record GetEndorsement(Guid EndorsementId) : IQuery<EndorsementView>;

public sealed class IssuePolicyValidator : AbstractValidator<IssuePolicy>
{
    public IssuePolicyValidator()
    {
        RuleFor(c => c.BillingFrequency).NotEmpty().MaximumLength(15);
        RuleFor(c => c.CollectionMethod).NotEmpty().MaximumLength(15);
    }
}

public sealed class CancelPolicyValidator : AbstractValidator<CancelPolicy>
{
    public CancelPolicyValidator() => RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(15);
}

public sealed class DraftEndorsementValidator : AbstractValidator<DraftEndorsement>
{
    public DraftEndorsementValidator()
    {
        RuleFor(c => c.EndorsementType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Changes).NotNull();
        // B-001: a broker of record change changes nothing in the cover; its handler refuses any change.
        RuleFor(c => c.Changes).NotEmpty().When(c => c.EndorsementType != GeneralEndorsement.IntermediaryType);
        RuleForEach(c => c.Changes).ChildRules(change =>
        {
            change.RuleFor(x => x.RiskObjectId).NotEmpty();
            change.RuleFor(x => x.SumInsured).PrecisionScale(18, 2, true);
            change.RuleForEach(x => x.Coverages).ChildRules(c =>
            {
                c.RuleFor(x => x.CoverageCode).NotEmpty().MaximumLength(20);
                c.RuleFor(x => x.SumInsured).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, true);
            });
        });
    }
}

internal static class PolicyNumbers
{
    /// <summary>TODO(spec): numbers are the scheme's prefix and an ID-based suffix; sequences and check digits need a counter (OQ-058).</summary>
    public static async Task<Result<string>> Next(IGeneralCodes codes, Guid id, DateOnly day, CancellationToken cancellationToken) =>
        await codes.PolicyNumberPrefix(day, cancellationToken) is { } prefix
            ? References.Of(prefix, id, 15)
            : Error.Validation(GeneralErrorCodes.CodeNotInForce, "No General policy numbering scheme is in force.");
}

internal sealed class IssuePolicyHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, Party.Contracts.IDueDiligenceStatus dueDiligence,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<IssuePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(IssuePolicy command, CancellationToken cancellationToken)
    {
        if (await general.Quote(command.QuoteId, cancellationToken) is not { } quote)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
        }

        if (await general.PolicyFromQuote(quote.Id, cancellationToken) is { } existing)
        {
            return PolicyView.From(existing);
        }

        // §5 (OQ-123): issue is allowed by default for General; a tenant may gate it.
        var cleared = await dueDiligence.ClearedFor(tenant.RequireTenantId(), quote.ProposerPartyId, Party.Contracts.DueDiligenceSteps.Issue, "GENERAL", cancellationToken);
        if (!cleared.Cleared)
        {
            return DueDiligenceRefusal.For(cleared.Outstanding, false);
        }

        // The product version is still on sale when the policy is issued.
        var product = await Products.Sellable(products, tenant.RequireTenantId(), quote.ProductCode, quote.ProductVersion, businessDate.Today, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        if (command.RenewalOfPolicyId is { } renewalOf && await general.Policy(renewalOf, cancellationToken) is not { Status: PolicyStatus.InForce })
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Only a policy in force is renewed.");
        }

        var id = Guid.CreateVersion7();
        var number = await PolicyNumbers.Next(codes, id, businessDate.Today, cancellationToken);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var billing = new BillingArrangement(command.BillingAccountId ?? Guid.CreateVersion7(), command.PayerPartyId ?? quote.ProposerPartyId, command.BillingFrequency,
            command.CollectionMethod);
        var policy = GeneralPolicy.IssuePolicy(id, number.Value, quote, billing, command.RenewalOfPolicyId, businessDate.Today, clock.UtcNow, product.Value.CoverStartsOn);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        general.Add(policy.Value);
        audit.Record(new AuditEntry("GENERAL", "IssuePolicy", nameof(GeneralPolicy), id.ToString(), new Dictionary<string, string?> { ["quote"] = quote.Id.ToString() }));
        return PolicyView.From(policy.Value);
    }
}

internal sealed class RenewPolicyHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<RenewPolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(RenewPolicy command, CancellationToken cancellationToken)
    {
        if (await general.Policy(command.PolicyId, cancellationToken) is not { } expiring)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        if (await general.RenewalOf(expiring.Id, cancellationToken) is { } renewed)
        {
            return PolicyView.From(renewed);
        }

        // Renewal follows the product's renewal rule (GEN-004): one that re-rates is renewed through a new quote.
        if (await codes.RenewalRule(expiring.ProductCode, businessDate.Today, cancellationToken) is not { } rule)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{expiring.ProductCode} has no renewal rule in force.");
        }

        // Wave 4 §4.2: a reviewed product renews on its review's offered terms (re-rated or not), and only once offered. §4.4: any
        // renewal on record renews only offered (not awaiting the customer's acceptance) or accepted; declined, not renewed by
        // the insurer, or lapsed, it does not.
        var review = await general.RenewalOfPeriod(expiring.Id, expiring.PeriodEnd, cancellationToken);
        if ((rule.Review && review is null) || (review is not null && (review.AwaitsAcceptance
                || review.Status is not (Domain.Renewals.RenewalStatus.Offered or Domain.Renewals.RenewalStatus.Accepted))))
        {
            var state = review is null ? "not reviewed" : review.AwaitsAcceptance ? "awaiting the customer's acceptance" : review.Status.ToString();
            return Error.Conflict(GeneralErrorCodes.RenewalNotOffered, $"Policy {expiring.PolicyNumber}'s renewal is {state}; it does not renew now.");
        }

        if (rule.ReRating && review is null)
        {
            return Error.Validation(GeneralErrorCodes.RenewalNeedsRating, $"{expiring.ProductCode} renewals are re-rated: quote again and issue with renewalOfPolicyId.");
        }

        var id = Guid.CreateVersion7();
        var number = await PolicyNumbers.Next(codes, id, businessDate.Today, cancellationToken);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var renewal = GeneralPolicy.RenewPolicy(id, number.Value, expiring, businessDate.Today, clock.UtcNow, review?.OfferedPremium);
        if (renewal.IsFailure)
        {
            return renewal.Error;
        }

        review?.MarkRenewed(id, clock.UtcNow);

        general.Add(renewal.Value);
        audit.Record(new AuditEntry("GENERAL", "RenewPolicy", nameof(GeneralPolicy), expiring.Id.ToString(), new Dictionary<string, string?> { ["renewal"] = id.ToString() }));
        return PolicyView.From(renewal.Value);
    }
}

internal sealed class CancelPolicyHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<CancelPolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(CancelPolicy command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        if (await codes.CancellationRule(command.ReasonCode, businessDate.Today, cancellationToken) is not { } rule)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a cancellation reason in force.");
        }

        audit.Record(new AuditEntry("GENERAL", "CancelPolicy", nameof(GeneralPolicy), policy.Id.ToString(), new Dictionary<string, string?> { ["reason"] = rule.ReasonCode }));
        var result = policy.CancelPolicy(rule, command.EffectiveDate, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy);
    }
}

internal sealed class ReinstatePolicyHandler(IGeneralRepository general, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<ReinstatePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(ReinstatePolicy command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        audit.Record(new AuditEntry("GENERAL", "ReinstatePolicy", nameof(GeneralPolicy), policy.Id.ToString()));
        var result = policy.ReinstatePolicy(user.Value, businessDate.Today, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy);
    }
}

internal sealed class GetPolicyHandler(IGeneralRepository general) : IQueryHandler<GetPolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(GetPolicy query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is { } policy
            ? PolicyView.From(policy) with
            {
                ProposalId = policy.QuoteId is { } quote ? (await general.ProposalForQuote(quote, cancellationToken))?.Id : null, // BR-035
                Effects = PolicyEffectsView.From(policy, await general.Effects(policy.Id, cancellationToken)), // BR-038
            }
            : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
}

internal sealed class DraftEndorsementHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate, IAuditTrail audit,
    Distribution.Contracts.IIntermediaries intermediaries, Documents.Contracts.IDocumentOwnership documents, ITenantContext tenant)
    : ICommandHandler<DraftEndorsement, EndorsementView>, ICommandHandler<RecordAppointmentLetter, EndorsementView>
{
    /// <summary>B-001: the appointment letter is a document filed against the endorsement.</summary>
    public async Task<Result<EndorsementView>> Handle(RecordAppointmentLetter command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Endorsement(command.EndorsementId, cancellationToken) is not { } endorsement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The endorsement was not found.");
        }

        if (!await documents.FiledAgainst(command.DocumentId, Contracts.GeneralDocuments.EndorsementRefType, endorsement.Id, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "documentId is not a document filed against this endorsement.");
        }

        var recorded = endorsement.RecordAppointmentLetter(command.DocumentId);
        audit.Record(new AuditEntry("GENERAL", "RecordAppointmentLetter", nameof(GeneralEndorsement), endorsement.Id.ToString(),
            new Dictionary<string, string?> { ["document"] = command.DocumentId.ToString() }));
        // The view is built before the save moves the version: the client's next If-Match is the version after it (BR-121).
        return recorded.IsFailure ? recorded.Error : EndorsementView.From(endorsement) with { RowVersion = endorsement.RowVersion + 1 };
    }

    public async Task<Result<EndorsementView>> Handle(DraftEndorsement command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Policy(command.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
        }

        if (await codes.EndorsementType(command.EndorsementType, businessDate.Today, cancellationToken) is not { } type)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.EndorsementType} is not an endorsement type in force.");
        }

        // B-001: a broker of record change names an intermediary that may sell the product, and changes nothing else.
        if (type.Code == GeneralEndorsement.IntermediaryType)
        {
            if (command.IntermediaryCode is not { Length: > 0 } intermediary || command.Changes.Count > 0)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, "An INTERMEDIARY endorsement names intermediaryCode and has no changes.");
            }

            if (await intermediaries.CanSell(tenant.RequireTenantId(), intermediary, policy.ProductCode, Distribution.Contracts.IntermediaryTransactions.NewBusiness,
                    command.EffectiveDate, cancellationToken) is { } refusal)
            {
                return Error.Validation(refusal.Code, refusal.Message);
            }

            var count = await general.EndorsementCount(policy.Id, cancellationToken) + 1;
            var change = GeneralEndorsement.DraftIntermediaryChange(Guid.CreateVersion7(), $"{policy.PolicyNumber}-E{count:D3}", policy, type, command.EffectiveDate,
                intermediary, user.Value);
            if (change.IsFailure)
            {
                return change.Error;
            }

            general.Add(change.Value);
            audit.Record(new AuditEntry("GENERAL", "DraftEndorsement", nameof(GeneralEndorsement), change.Value.Id.ToString(),
                new Dictionary<string, string?> { ["intermediary"] = intermediary }));
            return EndorsementView.From(change.Value);
        }

        if (command.IntermediaryCode is not null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"intermediaryCode is for an {GeneralEndorsement.IntermediaryType} endorsement.");
        }

        foreach (var change in command.Changes.Where(c => c.Action == EndorsementAction.AttachRisk))
        {
            if (await general.RiskObject(change.RiskObjectId, cancellationToken) is not { Status: RiskObjectStatus.Active })
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Risk object {change.RiskObjectId} is not an active risk object.");
            }
        }

        var sequence = await general.EndorsementCount(policy.Id, cancellationToken) + 1;
        var endorsement = GeneralEndorsement.DraftEndorsement(Guid.CreateVersion7(), $"{policy.PolicyNumber}-E{sequence:D3}", policy, type, command.EffectiveDate,
            command.Changes, user.Value);
        if (endorsement.IsFailure)
        {
            return endorsement.Error;
        }

        general.Add(endorsement.Value);
        audit.Record(new AuditEntry("GENERAL", "DraftEndorsement", nameof(GeneralEndorsement), endorsement.Value.Id.ToString()));
        return EndorsementView.From(endorsement.Value);
    }
}

internal sealed class SubmitEndorsementHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock,
    IAuditTrail audit) : ICommandHandler<SubmitEndorsement, EndorsementView>
{
    public async Task<Result<EndorsementView>> Handle(SubmitEndorsement command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Endorsement(command.EndorsementId, cancellationToken) is not { } endorsement
            || await general.Policy(endorsement.PolicyId, cancellationToken) is not { } policy)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The endorsement was not found.");
        }

        // F062 §1: sum insured the endorsement adds, by risk; the approver is told which zone limit it would exceed.
        var added = endorsement.Changes.Select(c => (c.RiskObjectId, Added: c.Action switch
        {
            EndorsementAction.AttachRisk => c.SumInsured ?? 0m,
            EndorsementAction.ChangeRisk => (c.SumInsured ?? 0m) - policy.Risks.Where(r => r.RiskObjectId == c.RiskObjectId && r.AttachedOn(endorsement.EffectiveDate)).Sum(r => r.SumInsured),
            _ => 0m,
        })).ToList();
        var exceeded = await ZoneExposure.FirstExceeded(general, codes, added, policy.Currency, businessDate.Today, cancellationToken);
        audit.Record(new AuditEntry("GENERAL", "SubmitEndorsement", nameof(GeneralEndorsement), endorsement.Id.ToString()));
        var result = endorsement.SubmitEndorsement(user.Value, policy.Currency, clock.UtcNow, exceeded?.ToString());
        return result.IsFailure ? result.Error : EndorsementView.From(endorsement);
    }
}

internal sealed class GetEndorsementHandler(IGeneralRepository general, Workflow.Contracts.IApprovalHistory history) : IQueryHandler<GetEndorsement, EndorsementView>
{
    public async Task<Result<EndorsementView>> Handle(GetEndorsement query, CancellationToken cancellationToken) =>
        await general.Endorsement(query.EndorsementId, cancellationToken) is { } endorsement
            ? EndorsementView.From(endorsement) with
            {
                LatestRejection = await history.Latest(Contracts.GeneralApprovals.EndorsementBusinessRefType, endorsement.Id, cancellationToken),
            }
            : Error.NotFound(ErrorCodes.NotFound, "The endorsement was not found.");
}
