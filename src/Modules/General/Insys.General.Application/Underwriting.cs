using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Distribution.Contracts;
using Insys.General.Domain;
using Insys.General.Domain.Underwriting;
using Insys.ProductFactory.Contracts;

namespace Insys.General.Application;

/// <summary>Command RegisterRiskObject: its attributes conform to the type's schema version (GEN-005).</summary>
public sealed record RegisterRiskObject(string ObjectType, JsonElement Attributes, decimal? Valuation, string? ValuationBasis, string? Subdivision, string? PostalCode,
    string? RiskZone) : ICommand<RiskObjectView>;

public sealed record UpdateRiskAttributes(Guid RiskObjectId, JsonElement Attributes) : ICommand<RiskObjectView>;

public sealed record RevalueRiskObject(Guid RiskObjectId, decimal? Valuation, string? ValuationBasis) : ICommand<RiskObjectView>;

public sealed record DeactivateRiskObject(Guid RiskObjectId) : ICommand<RiskObjectView>;

public sealed record GetRiskObject(Guid RiskObjectId) : IQuery<RiskObjectView>;

/// <summary>Command CreateQuote: a new quote, or (with <paramref name="FromQuoteId"/>) the next version of an existing one.</summary>
public sealed record CreateQuote(string ProductCode, int? ProductVersion, Guid ProposerPartyId, string? IntermediaryCode, string Currency, DateOnly PeriodStart,
    DateOnly PeriodEnd, IReadOnlyList<RiskRequest> Risks, Guid? FromQuoteId) : ICommand<QuoteView>;

public enum QuoteAction
{
    Issue,
    Accept,
    Reject,
}

public sealed record ChangeQuote(Guid QuoteId, QuoteAction Action) : ICommand<QuoteView>;

public sealed record ReferQuote(Guid QuoteId, string ReasonCode) : ICommand<QuoteView>;

/// <param name="DeclineReason">Why, when declined: the quote is then Declined for good.</param>
public sealed record DecideReferral(Guid QuoteId, bool Approved, string DecisionCode, IReadOnlyList<string> SpecialConditions, string? DeclineReason = null) : ICommand<QuoteView>;

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

internal sealed class RegisterRiskObjectHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<RegisterRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(RegisterRiskObject command, CancellationToken cancellationToken)
    {
        var schema = await RiskSchemas.Load(codes, command.ObjectType, clock.UtcToday, cancellationToken);
        var values = Attributes.Read(command.Attributes);
        if (schema.IsFailure || values.IsFailure)
        {
            return schema.IsFailure ? schema.Error : values.Error!;
        }

        if (command.ValuationBasis is { } basis && !await codes.Exists(GeneralCode.ValuationBasis, basis, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{basis} is not a valuation basis in force.");
        }

        var risk = RiskObject.RegisterRiskObject(Guid.CreateVersion7(), schema.Value, values.Value, command.Attributes.GetRawText(), command.Valuation, command.ValuationBasis,
            new RiskLocation(command.Subdivision, command.PostalCode, command.RiskZone), clock.UtcToday, clock.UtcNow);
        if (risk.IsFailure)
        {
            return risk.Error;
        }

        general.Add(risk.Value);
        audit.Record(new AuditEntry("GENERAL", "RegisterRiskObject", nameof(RiskObject), risk.Value.Id.ToString()));
        return RiskObjectView.From(risk.Value);
    }
}

internal sealed class UpdateRiskAttributesHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock) : ICommandHandler<UpdateRiskAttributes, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(UpdateRiskAttributes command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        var schema = await RiskSchemas.Load(codes, risk.ObjectType, clock.UtcToday, cancellationToken);
        var values = Attributes.Read(command.Attributes);
        if (schema.IsFailure || values.IsFailure)
        {
            return schema.IsFailure ? schema.Error : values.Error!;
        }

        var result = risk.UpdateAttributes(schema.Value, values.Value, command.Attributes.GetRawText(), clock.UtcToday);
        return result.IsFailure ? result.Error : RiskObjectView.From(risk);
    }
}

internal sealed class RevalueRiskObjectHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock) : ICommandHandler<RevalueRiskObject, RiskObjectView>
{
    public async Task<Result<RiskObjectView>> Handle(RevalueRiskObject command, CancellationToken cancellationToken)
    {
        if (await general.RiskObject(command.RiskObjectId, cancellationToken) is not { } risk)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The risk object was not found.");
        }

        if (command.ValuationBasis is { } basis && !await codes.Exists(GeneralCode.ValuationBasis, basis, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{basis} is not a valuation basis in force.");
        }

        var result = risk.RevalueRiskObject(command.Valuation, command.ValuationBasis);
        return result.IsFailure ? result.Error : RiskObjectView.From(risk);
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

internal sealed class CreateQuoteHandler(IGeneralRepository general, ISellableProducts products, IIntermediaries intermediaries, ITenantContext tenant, IClock clock,
    IAuditTrail audit)
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

        var product = await Products.Sellable(products, tenant.RequireTenantId(), command.ProductCode, command.ProductVersion, clock.UtcToday, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        // DST-001: a quote through an intermediary needs one that may sell the product.
        if (command.IntermediaryCode is { Length: > 0 } intermediary
            && await intermediaries.CanSell(tenant.RequireTenantId(), intermediary, product.Value.ProductCode, IntermediaryTransactions.NewBusiness, clock.UtcToday,
                cancellationToken) is { } refusal)
        {
            return Error.Validation(refusal.Code, refusal.Message);
        }

        foreach (var riskId in command.Risks.Select(r => r.RiskObjectId))
        {
            if (await general.RiskObject(riskId, cancellationToken) is not { Status: RiskObjectStatus.Active })
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Risk object {riskId} is not an active risk object.");
            }
        }

        // A draft is valid only once issued, for the product's quote validity period; until then the date is today.
        var id = Guid.CreateVersion7();
        var quote = GeneralQuote.CreateQuote(id, "GQ-" + id.ToString("N")[..12].ToUpperInvariant(), previous, product.Value, command.ProposerPartyId, command.IntermediaryCode,
            command.Currency, command.PeriodStart, command.PeriodEnd, command.Risks, clock.UtcToday);
        if (quote.IsFailure)
        {
            return quote.Error;
        }

        general.Add(quote.Value);
        audit.Record(new AuditEntry("GENERAL", "CreateQuote", nameof(GeneralQuote), id.ToString()));
        return QuoteView.From(quote.Value);
    }
}

internal sealed class ChangeQuoteHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<ChangeQuote, QuoteView>
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
                if (await codes.QuoteValidityDays(quote.ProductCode, clock.UtcToday, cancellationToken) is not { } days)
                {
                    return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{quote.ProductCode} has no quote validity period in force.");
                }

                result = quote.IssueQuote(clock.UtcToday.AddDays(days), clock.UtcNow);
                break;
            case QuoteAction.Accept:
                result = quote.AcceptQuote(clock.UtcToday, clock.UtcNow);
                break;
            default:
                result = quote.RejectQuote();
                break;
        }

        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

internal sealed class ReferQuoteHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IClock clock) : ICommandHandler<ReferQuote, QuoteView>
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

        if (!await codes.Exists(GeneralCode.ReferralReason, command.ReasonCode, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a referral reason in force.");
        }

        var result = quote.ReferQuote(command.ReasonCode, user.Value);
        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

internal sealed class DecideReferralHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IClock clock, IAuditTrail audit)
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

        if (!await codes.Exists(GeneralCode.UnderwritingDecision, command.DecisionCode, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.DecisionCode} is not an underwriting decision code in force.");
        }

        audit.Record(new AuditEntry("GENERAL", "DecideReferral", nameof(GeneralQuote), quote.Id.ToString(),
            new Dictionary<string, string?> { ["approved"] = command.Approved.ToString(), ["decision"] = command.DecisionCode }));
        var result = quote.DecideReferral(command.Approved, command.DecisionCode, command.SpecialConditions, command.DeclineReason, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : QuoteView.From(quote);
    }
}

internal sealed class GetQuoteHandler(IGeneralRepository general) : IQueryHandler<GetQuote, QuoteView>
{
    public async Task<Result<QuoteView>> Handle(GetQuote query, CancellationToken cancellationToken) =>
        await general.Quote(query.QuoteId, cancellationToken) is { } quote ? QuoteView.From(quote) : Error.NotFound(ErrorCodes.NotFound, "The quote was not found.");
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
        await general.Proposal(query.ProposalId, cancellationToken) is { } proposal ? ProposalView.From(proposal) : Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
}
