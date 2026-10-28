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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Underwriting;
using Insys.Party.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

/// <summary>Command RegisterRiskObject: its attributes conform to the type's schema version (GEN-005).</summary>
/// <param name="ValuationCurrency">The valuation's currency (BR-033), a 3-letter code: required with a valuation, and only with one.</param>
/// <param name="OwnerPartyId">The client the item belongs to (BR-092), a registered party; none when not known.</param>
public sealed record RegisterRiskObject([property: CodeList(CodeLists.RiskObjectTypes)] string ObjectType, JsonElement Attributes, decimal? Valuation,
    [property: CodeList(CodeLists.ValuationBases)] string? ValuationBasis, string? Subdivision, string? PostalCode,
    [property: CodeList(CodeLists.RiskZones)] string? RiskZone, string? ValuationCurrency = null, Guid? OwnerPartyId = null) : ICommand<RiskObjectView>;

/// <summary>Command ChangeRiskObjectOwner (BR-092): staff set, change or clear the client an item belongs to; audited with the owner before and after.</summary>
public sealed record ChangeRiskObjectOwner(Guid RiskObjectId, Guid? OwnerPartyId) : ICommand<RiskObjectView>;

public sealed record UpdateRiskAttributes(Guid RiskObjectId, JsonElement Attributes) : ICommand<RiskObjectView>;

public sealed record RevalueRiskObject(Guid RiskObjectId, decimal? Valuation, [property: CodeList(CodeLists.ValuationBases)] string? ValuationBasis,
    string? ValuationCurrency = null) : ICommand<RiskObjectView>;

public sealed record DeactivateRiskObject(Guid RiskObjectId) : ICommand<RiskObjectView>;

public sealed record GetRiskObject(Guid RiskObjectId) : IQuery<RiskObjectView>;

/// <summary>Command CreateQuote: a new quote, or (with <paramref name="FromQuoteId"/>) the next version of an existing one.</summary>
/// <param name="OrgNodeCode">
/// The organisation node (normally a branch) that owns the quote, and the policy and claims that follow from it (F103).
/// When omitted: the caller's one node, else the tenant's root legal entity. A later version keeps the first one's node.
/// </param>
public sealed record CreateQuote(string ProductCode, int? ProductVersion, Guid ProposerPartyId, string? IntermediaryCode, string Currency, DateOnly PeriodStart,
    DateOnly PeriodEnd, IReadOnlyList<RiskRequest> Risks, Guid? FromQuoteId, string? OrgNodeCode = null) : ICommand<QuoteView>;

public enum QuoteAction
{
    Issue,
    Accept,
    Reject,
}

public sealed record ChangeQuote(Guid QuoteId, QuoteAction Action) : ICommand<QuoteView>;

public sealed record ReferQuote(Guid QuoteId, [property: CodeList(CodeLists.ReferralReasons)] string ReasonCode) : ICommand<QuoteView>;

/// <param name="DeclineReason">Why, when declined: the quote is then Declined for good.</param>
public sealed record DecideReferral(Guid QuoteId, bool Approved, [property: CodeList(CodeLists.UnderwritingDecisions)] string DecisionCode,
    IReadOnlyList<string> SpecialConditions, string? DeclineReason = null) : ICommand<QuoteView>;

public sealed record GetQuote(Guid QuoteId) : IQuery<QuoteView>;

public sealed record SubmitProposal(Guid QuoteId, JsonElement Declarations) : ICommand<ProposalView>;

public sealed record AcceptProposal(Guid ProposalId) : ICommand<ProposalView>;

public sealed record GetProposal(Guid ProposalId) : IQuery<ProposalView>;

public sealed class RegisterRiskObjectValidator : AbstractValidator<RegisterRiskObject>
{
    public RegisterRiskObjectValidator()
    {
        RuleFor(c => c.ObjectType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Valuation).PrecisionScale(18, 2, true);
        RuleFor(c => c.ValuationBasis).MaximumLength(15);
        RuleFor(c => c.Subdivision).MaximumLength(6);
        RuleFor(c => c.PostalCode).MaximumLength(10);
        RuleFor(c => c.RiskZone).MaximumLength(10);
    }
}

public sealed class CreateQuoteValidator : AbstractValidator<CreateQuote>
{
    public CreateQuoteValidator()
    {
        RuleFor(c => c.ProductCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.ProposerPartyId).NotEmpty();
        RuleFor(c => c.IntermediaryCode).MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Risks).NotEmpty();
        RuleForEach(c => c.Risks).ChildRules(r =>
        {
            r.RuleFor(x => x.RiskObjectId).NotEmpty();
            r.RuleFor(x => x.SumInsured).GreaterThan(0).PrecisionScale(18, 2, true);
            r.RuleFor(x => x.Coverages).NotEmpty();
            r.RuleForEach(x => x.Coverages).ChildRules(c =>
            {
                c.RuleFor(x => x.CoverageCode).NotEmpty().MaximumLength(20);
                c.RuleFor(x => x.SumInsured).GreaterThan(0).PrecisionScale(18, 2, true);
                c.RuleFor(x => x.ExcessScheduleCode).MaximumLength(20);
            });
        });
    }
}

public sealed class ReferQuoteValidator : AbstractValidator<ReferQuote>
{
    public ReferQuoteValidator() => RuleFor(c => c.ReasonCode).NotEmpty().MaximumLength(15);
}

public sealed class DecideReferralValidator : AbstractValidator<DecideReferral>
{
    public DecideReferralValidator() => RuleFor(c => c.DecisionCode).NotEmpty().MaximumLength(15);
}

internal sealed class RegisterRiskObjectHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit,
    IPartyDirectory parties, ITenantContext tenant) : ICommandHandler<RegisterRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(RegisterRiskObject command, CancellationToken cancellationToken)
    {
        if (command.OwnerPartyId is { } owner && !await parties.Exists(tenant.RequireTenantId(), owner, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The owner is not a party of the tenant.");
        }

        var schema = await RiskSchemas.Load(codes, command.ObjectType, businessDate.Today, cancellationToken);
        var values = Attributes.Read(command.Attributes);
        if (schema.IsFailure || values.IsFailure)
        {
            return schema.IsFailure ? schema.Error : values.Error!;
        }

        if (command.ValuationBasis is { } basis && !await codes.Exists(GeneralCode.ValuationBasis, basis, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{basis} is not a valuation basis in force.");
        }

        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema.Value, values.Value, command.Attributes.GetRawText(), command.Valuation, command.ValuationBasis,
            new RiskLocation(command.Subdivision, command.PostalCode, command.RiskZone), businessDate.Today, clock.UtcNow, command.ValuationCurrency?.ToUpperInvariant(),
            command.OwnerPartyId);
        if (risk.IsFailure)
        {
            return risk.Error;
        }

        general.Add(risk.Value);
        audit.Record(new AuditEntry("GENERAL", "RegisterRiskObject", nameof(RiskObject), risk.Value.Id.ToString()));
        return RiskObjectView.From(risk.Value);
    }
}

internal sealed class UpdateRiskAttributesHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate) : ICommandHandler<UpdateRiskAttributes, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(UpdateRiskAttributes command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        var schema = await RiskSchemas.Load(codes, risk.ObjectType, businessDate.Today, cancellationToken);
        var values = Attributes.Read(command.Attributes);
        if (schema.IsFailure || values.IsFailure)
        {
            return schema.IsFailure ? schema.Error : values.Error!;
        }

        var result = risk.UpdateAttributes(schema.Value, values.Value, command.Attributes.GetRawText(), businessDate.Today);
        return result.IsFailure ? result.Error : RiskObjectView.From(risk);
    }
}

internal sealed class RevalueRiskObjectHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate) : ICommandHandler<RevalueRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(RevalueRiskObject command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        if (command.ValuationBasis is { } basis && !await codes.Exists(GeneralCode.ValuationBasis, basis, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{basis} is not a valuation basis in force.");
        }

        var result = risk.RevalueRiskObject(command.Valuation, command.ValuationBasis, command.ValuationCurrency?.ToUpperInvariant());
        return result.IsFailure ? result.Error : RiskObjectView.From(risk);
    }
}

internal sealed class ChangeRiskObjectOwnerHandler(IGeneralRepository general, IAuditTrail audit, IPartyDirectory parties, ITenantContext tenant)
    : ICommandHandler<ChangeRiskObjectOwner, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(ChangeRiskObjectOwner command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        if (command.OwnerPartyId is { } owner && !await parties.Exists(tenant.RequireTenantId(), owner, cancellationToken))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The owner is not a party of the tenant.");
        }

        var before = risk.OwnerPartyId;
        var result = risk.ChangeOwner(command.OwnerPartyId);
        if (result.IsFailure)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("GENERAL", "ChangeRiskObjectOwner", nameof(RiskObject), risk.Id.ToString(),
            new Dictionary<string, string?> { ["owner_before"] = before?.ToString(), ["owner_after"] = command.OwnerPartyId?.ToString() }));
        return RiskObjectView.From(risk);
    }
}

internal sealed class DeactivateRiskObjectHandler(IGeneralRepository general) : ICommandHandler<DeactivateRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(DeactivateRiskObject command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        var result = risk.Deactivate();
        return result.IsFailure ? result.Error : RiskObjectView.From(risk);
    }
}

internal sealed class GetRiskObjectHandler(IGeneralRepository general) : IQueryHandler<GetRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(GetRiskObject query, CancellationToken cancellationToken) =>
        await general.RiskObject(query.RiskObjectId, cancellationToken) is { } risk ? RiskObjectView.From(risk) : Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
}

internal static class RiskSchemas
{
    public static async Task<Result<AttributeSchema>> Load(IGeneralCodes codes, string objectType, DateOnly day, CancellationToken cancellationToken) =>
        await codes.RiskObjectType(objectType, day, cancellationToken) is { } type
            ? AttributeSchema.Parse(type.ObjectType, type.Version, type.Attributes, type.Validation)
            : Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{objectType} is not a risk object type in force.");
}

/// <summary>
/// The rating inputs of a quote priced as it is made (BR-095): its total sum insured as <c>sum_insured</c>, and its risk
/// objects' attributes by name, as the rating factors read them (rating.cfg_rating_factor_definitions.source_attribute).
/// Risk objects giving one input two values are not guessed between: the quote stays Draft with the reason.
/// </summary>
public static class QuotePricing
{
    /// <summary>A General quote's premium is annual (the policy's annual premium; its billing frequency is chosen at issue).</summary>
    public const string Frequency = "ANNUAL";

    public static Result<string> Values(IReadOnlyList<RiskObject> objects, decimal sumInsured)
    {
        var values = new System.Text.Json.Nodes.JsonObject();
        foreach (var riskObject in objects)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(riskObject.Attributes) is not System.Text.Json.Nodes.JsonObject attributes)
            {
                continue;
            }

            foreach (var (name, value) in attributes)
            {
                if (values.TryGetPropertyValue(name, out var existing))
                {
                    if (!System.Text.Json.Nodes.JsonNode.DeepEquals(existing, value))
                    {
                        return Error.Validation(GeneralErrorCodes.RatingInputsDiffer,
                            $"The quote's risk objects give {name} different values, so it is not priced automatically; staff rate it.");
                    }

                    continue;
                }

                values[name] = value?.DeepClone();
            }
        }

        values["sum_insured"] = sumInsured;
        return values.ToJsonString();
    }
}

internal sealed class CreateQuoteHandler(IGeneralRepository general, ISellableProducts products, IIntermediaries intermediaries, ITenantContext tenant, IBusinessDate businessDate,
    IAuditTrail audit, IExecutionContext execution, Tenancy.Contracts.IOrganisationStructure organisation, IClock clock)
    : ICommandHandler<CreateQuote, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(CreateQuote command, CancellationToken cancellationToken)
    {
        GeneralQuote? previous = null;
        if (command.FromQuoteId is { } fromId)
        {
            if (await general.Quote(fromId, cancellationToken) is not { } from)
            {
                return Error.NotFound(ErrorCodes.NotFound, "The quote to revise was not found.");
            }

            previous = await general.LatestQuoteVersion(from.QuoteNumber, cancellationToken);
        }

        var product = await Products.Sellable(products, tenant.RequireTenantId(), command.ProductCode, command.ProductVersion, businessDate.Today, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        // DST-001: a quote through an intermediary needs one that may sell the product.
        if (command.IntermediaryCode is { Length: > 0 } intermediary
            && await intermediaries.CanSell(tenant.RequireTenantId(), intermediary, product.Value.ProductCode, IntermediaryTransactions.NewBusiness, businessDate.Today,
                cancellationToken) is { } refusal)
        {
            return Error.Validation(refusal.Code, refusal.Message);
        }

        var objects = new List<RiskObject>();
        foreach (var riskId in command.Risks.Select(r => r.RiskObjectId))
        {
            if (await general.RiskObject(riskId, cancellationToken) is not { Status: RiskObjectStatus.Active } riskObject)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Risk object {riskId} is not an active risk object.");
            }

            objects.Add(riskObject);
        }

        // A draft is valid only once issued, for the product's quote validity period; until then the date is today.
        var id = Guid.CreateVersion7();
        var quote = GeneralQuote.CreateQuote(id, References.Of("GQ-", id, 15), previous, product.Value, command.ProposerPartyId, command.IntermediaryCode,
            command.Currency, command.PeriodStart, command.PeriodEnd, command.Risks, businessDate.Today);
        if (quote.IsFailure)
        {
            return quote.Error;
        }

        // F103 (addenda/access-scopes.md §2): the owning node, which must be in the structure and, with the product, in the caller's scope.
        if (previous is null)
        {
            var nodes = await organisation.Nodes(tenant.RequireTenantId(), businessDate.Today, cancellationToken);
            var node = execution.PlaceNew(product.Value.ProductCode, command.OrgNodeCode, nodes.Select(n => n.NodeCode).ToList(), Tenancy.Contracts.OrganisationNodes.Root(nodes));
            if (node.IsFailure)
            {
                return node.Error;
            }

            quote.Value.PlaceAt(node.Value);
            quote.Value.RecordChannel(execution.ChannelCode);
        }

        // BR-095 (decided by Ade): a broker's portal quote, and every later version of it, is priced as it is made.
        if (execution.External is { Kind: ExternalSubject.Intermediary } || previous is { PricedOnCreation: true })
        {
            var values = QuotePricing.Values(objects, command.Risks.Sum(r => r.SumInsured));
            var priced = values.IsSuccess
                ? quote.Value.RequestPricing(values.Value, businessDate.Today, clock.UtcNow)
                : quote.Value.RecordRatingIssue(values.Error.Code, values.Error.Message);
            if (priced.IsFailure)
            {
                return priced.Error;
            }
        }

        general.Add(quote.Value);
        audit.Record(new AuditEntry("GENERAL", "CreateQuote", nameof(GeneralQuote), id.ToString()));
        return QuoteView.From(quote.Value);
    }
}

internal sealed class ChangeQuoteHandler(IGeneralRepository general, IGeneralCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit, IExecutionContext execution)
    : ICommandHandler<ChangeQuote, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(ChangeQuote command, CancellationToken cancellationToken)
    {
        if (await general.Quote(command.QuoteId, cancellationToken) is not { } quote)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
        }

        audit.Record(new AuditEntry("GENERAL", $"{command.Action}Quote", nameof(GeneralQuote), quote.Id.ToString()));
        Result result;
        switch (command.Action)
        {
            case QuoteAction.Issue:
                // The validity period counts from issue (general.cfg_quote_validity_periods).
                if (await codes.QuoteValidityDays(quote.ProductCode, businessDate.Today, cancellationToken) is not { } days)
                {
                    return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{quote.ProductCode} has no quote validity period in force.");
                }

                // F062 §1: over a zone and peril limit, the quote is referred to an underwriter instead; once they approve, it issues.
                if (!quote.ReferralApproved(ZoneExposure.ReferralReason)
                    && await ZoneExposure.FirstExceeded(general, codes, quote.Risks.Select(r => (r.RiskObjectId, r.SumInsured)).ToList(), quote.Currency, businessDate.Today,
                        cancellationToken) is { } exceeded)
                {
                    var user = Users.Require(execution);
                    if (user.IsFailure)
                    {
                        return user.Error;
                    }

                    audit.Record(new AuditEntry("GENERAL", "ReferQuote", nameof(GeneralQuote), quote.Id.ToString(),
                        new Dictionary<string, string?> { ["reason"] = ZoneExposure.ReferralReason, ["limit"] = exceeded.ToString() }));
                    result = quote.ReferQuote(ZoneExposure.ReferralReason, user.Value);
                    break;
                }

                result = quote.IssueQuote(businessDate.Today.AddDays(days), clock.UtcNow);
                break;
            case QuoteAction.Accept:
                result = quote.AcceptQuote(businessDate.Today, clock.UtcNow);
                break;
            default:
                result = quote.RejectQuote();
                break;
        }

        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

internal sealed class ReferQuoteHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate) : ICommandHandler<ReferQuote, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(ReferQuote command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Quote(command.QuoteId, cancellationToken) is not { } quote)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
        }

        if (!await codes.Exists(GeneralCode.ReferralReason, command.ReasonCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a referral reason in force.");
        }

        var result = quote.ReferQuote(command.ReasonCode, user.Value);
        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

/// <summary>
/// Command DecideReferral, under the tenant's underwriting authority (§7.4): the underwriter's roles hold an authority in
/// rules.cfg_authority_matrices for the quote's product that covers its total sum insured and the decision code, whoever
/// referred it (the rules, staff or a broker's portal quote). Without one the decision is refused, and someone who holds
/// it decides.
/// </summary>
internal sealed class DecideReferralHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit,
    Identity.Contracts.IUserRoleQuery roles, Rules.Contracts.IUnderwritingAuthorities authorities, ITenantContext tenant)
    : ICommandHandler<DecideReferral, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(DecideReferral command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await general.Quote(command.QuoteId, cancellationToken) is not { } quote)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
        }

        if (!await codes.Exists(GeneralCode.UnderwritingDecision, command.DecisionCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.DecisionCode} is not an underwriting decision code in force.");
        }

        var sumInsured = quote.Risks.Sum(r => r.SumInsured);
        var tenantId = tenant.RequireTenantId();
        var authority = (await authorities.For(tenantId, await roles.EffectiveRoles(tenantId, user.Value, cancellationToken), quote.ProductCode, businessDate.Today,
                cancellationToken))
            .FirstOrDefault(a => a.MaxSumAssured >= sumInsured && a.DecisionTypes.Contains(command.DecisionCode, StringComparer.Ordinal));
        if (authority is null)
        {
            return Error.Forbidden(GeneralErrorCodes.UnderwritingAuthorityExceeded,
                $"Your underwriting authority does not cover a {command.DecisionCode} decision on {quote.ProductCode} for {sumInsured} {quote.Currency}.");
        }

        audit.Record(new AuditEntry("GENERAL", "DecideReferral", nameof(GeneralQuote), quote.Id.ToString(),
            new Dictionary<string, string?> { ["approved"] = command.Approved.ToString(), ["decision"] = command.DecisionCode, ["authority"] = authority.RoleCode }));
        var result = quote.DecideReferral(command.Approved, command.DecisionCode, command.SpecialConditions, command.DeclineReason, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

internal sealed class GetQuoteHandler(IGeneralRepository general) : IQueryHandler<GetQuote, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(GetQuote query, CancellationToken cancellationToken) =>
        await general.Quote(query.QuoteId, cancellationToken) is { } quote
            ? QuoteView.From(quote) with { ProposalId = (await general.ProposalForQuote(quote.Id, cancellationToken))?.Id } // BR-035
            : Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
}

internal sealed class SubmitProposalHandler(IGeneralRepository general, IClock clock) : ICommandHandler<SubmitProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(SubmitProposal command, CancellationToken cancellationToken)
    {
        if (await general.Quote(command.QuoteId, cancellationToken) is not { } quote)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
        }

        if (await general.ProposalForQuote(quote.Id, cancellationToken) is { } existing)
        {
            return ProposalView.From(existing);
        }

        if (command.Declarations.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Declarations are a JSON object.");
        }

        var proposal = GeneralProposal.SubmitProposal(Guid.CreateVersion7(), quote, command.Declarations.GetRawText(), clock.UtcNow);
        if (proposal.IsFailure)
        {
            return proposal.Error;
        }

        general.Add(proposal.Value);
        return ProposalView.From(proposal.Value);
    }
}

internal sealed class AcceptProposalHandler(IGeneralRepository general) : ICommandHandler<AcceptProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(AcceptProposal command, CancellationToken cancellationToken)
    {
        if (await general.Proposal(command.ProposalId, cancellationToken) is not { } proposal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
        }

        var result = proposal.AcceptProposal();
        return result.IsFailure ? result.Error : ProposalView.From(proposal);
    }
}

internal sealed class GetProposalHandler(IGeneralRepository general) : IQueryHandler<GetProposal, ProposalView>
{
    public async Task<Result<ProposalView>> Handle(GetProposal query, CancellationToken cancellationToken) =>
        await general.Proposal(query.ProposalId, cancellationToken) is { } proposal
            ? ProposalView.From(proposal) with { PolicyId = (await general.PolicyFromQuote(proposal.QuoteId, cancellationToken))?.Id } // BR-035
            : Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
}
