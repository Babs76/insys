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
public sealed record IssuePolicy(Guid QuoteId, Guid? BillingAccountId, Guid? PayerPartyId, string BillingFrequency, string CollectionMethod, Guid? RenewalOfPolicyId)
    : ICommand<PolicyView>;

/// <summary>Command RenewPolicy, where the product's renewal rule does not re-rate; a re-rated renewal is a new quote issued with <c>renewalOfPolicyId</c>.</summary>
public sealed record RenewPolicy(Guid PolicyId) : ICommand<PolicyView>;

public sealed record CancelPolicy(Guid PolicyId, string ReasonCode, DateOnly EffectiveDate) : ICommand<PolicyView>;

public sealed record ReinstatePolicy(Guid PolicyId) : ICommand<PolicyView>;

public sealed record GetPolicy(Guid PolicyId) : IQuery<PolicyView>;

/// <summary>Command DraftEndorsement: changes effective from a date within the policy period (GEN-003).</summary>
public sealed record DraftEndorsement(Guid PolicyId, string EndorsementType, DateOnly EffectiveDate, IReadOnlyList<EndorsementChange> Changes) : ICommand<EndorsementView>;

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
        RuleFor(c => c.Changes).NotEmpty();
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

internal sealed class IssuePolicyHandler(IGeneralRepository general, IGeneralCodes codes, ISellableProducts products, ITenantContext tenant, IClock clock, IAuditTrail audit)
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

        // The product version is still on sale when the policy is issued.
        var product = await Products.Sellable(products, tenant.RequireTenantId(), quote.ProductCode, quote.ProductVersion, clock.UtcToday, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        if (command.RenewalOfPolicyId is { } renewalOf && await general.Policy(renewalOf, cancellationToken) is not { Status: PolicyStatus.InForce })
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "Only a policy in force is renewed.");
        }

        var id = Guid.CreateVersion7();
        var number = await PolicyNumbers.Next(codes, id, clock.UtcToday, cancellationToken);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var billing = new BillingArrangement(command.BillingAccountId ?? Guid.CreateVersion7(), command.PayerPartyId ?? quote.ProposerPartyId, command.BillingFrequency,
            command.CollectionMethod);
        var policy = GeneralPolicy.IssuePolicy(id, number.Value, quote, billing, command.RenewalOfPolicyId, clock.UtcToday, clock.UtcNow, product.Value.CoverStartsOn);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        general.Add(policy.Value);
        audit.Record(new AuditEntry("GENERAL", "IssuePolicy", nameof(GeneralPolicy), id.ToString(), new Dictionary<string, string?> { ["quote"] = quote.Id.ToString() }));
        return PolicyView.From(policy.Value);
    }
}

internal sealed class RenewPolicyHandler(IGeneralRepository general, IGeneralCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<RenewPolicy, PolicyView>
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
        if (await codes.RenewalRule(expiring.ProductCode, clock.UtcToday, cancellationToken) is not { } rule)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{expiring.ProductCode} has no renewal rule in force.");
        }

        if (rule.ReRating)
        {
            return Error.Validation(GeneralErrorCodes.RenewalNeedsRating, $"{expiring.ProductCode} renewals are re-rated: quote again and issue with renewalOfPolicyId.");
        }

        var id = Guid.CreateVersion7();
        var number = await PolicyNumbers.Next(codes, id, clock.UtcToday, cancellationToken);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var renewal = GeneralPolicy.RenewPolicy(id, number.Value, expiring, clock.UtcToday, clock.UtcNow);
        if (renewal.IsFailure)
        {
            return renewal.Error;
        }

        general.Add(renewal.Value);
        audit.Record(new AuditEntry("GENERAL", "RenewPolicy", nameof(GeneralPolicy), expiring.Id.ToString(), new Dictionary<string, string?> { ["renewal"] = id.ToString() }));
        return PolicyView.From(renewal.Value);
    }
}

internal sealed class CancelPolicyHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IClock clock, IAuditTrail audit)
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

        if (await codes.CancellationRule(command.ReasonCode, clock.UtcToday, cancellationToken) is not { } rule)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a cancellation reason in force.");
        }

        audit.Record(new AuditEntry("GENERAL", "CancelPolicy", nameof(GeneralPolicy), policy.Id.ToString(), new Dictionary<string, string?> { ["reason"] = rule.ReasonCode }));
        var result = policy.CancelPolicy(rule, command.EffectiveDate, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy);
    }
}

internal sealed class ReinstatePolicyHandler(IGeneralRepository general, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<ReinstatePolicy, PolicyView>
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
        var result = policy.ReinstatePolicy(user.Value, clock.UtcToday, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy);
    }
}

internal sealed class GetPolicyHandler(IGeneralRepository general) : IQueryHandler<GetPolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(GetPolicy query, CancellationToken cancellationToken) =>
        await general.Policy(query.PolicyId, cancellationToken) is { } policy ? PolicyView.From(policy) : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
}

internal sealed class DraftEndorsementHandler(IGeneralRepository general, IGeneralCodes codes, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<DraftEndorsement, EndorsementView>
{
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

        if (await codes.EndorsementType(command.EndorsementType, clock.UtcToday, cancellationToken) is not { } type)
        {
            return Error.Validation(GeneralErrorCodes.CodeNotInForce, $"{command.EndorsementType} is not an endorsement type in force.");
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

internal sealed class SubmitEndorsementHandler(IGeneralRepository general, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<SubmitEndorsement, EndorsementView>
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

        audit.Record(new AuditEntry("GENERAL", "SubmitEndorsement", nameof(GeneralEndorsement), endorsement.Id.ToString()));
        var result = endorsement.SubmitEndorsement(user.Value, policy.Currency, clock.UtcNow);
        return result.IsFailure ? result.Error : EndorsementView.From(endorsement);
    }
}

internal sealed class GetEndorsementHandler(IGeneralRepository general) : IQueryHandler<GetEndorsement, EndorsementView>
{
    public async Task<Result<EndorsementView>> Handle(GetEndorsement query, CancellationToken cancellationToken) =>
        await general.Endorsement(query.EndorsementId, cancellationToken) is { } endorsement
            ? EndorsementView.From(endorsement)
            : Error.NotFound(ErrorCodes.NotFound, "The endorsement was not found.");
}
