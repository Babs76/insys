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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Identity.Contracts;
using Insys.Life.Contracts;
using Insys.Life.Domain;
using Insys.Life.Domain.Policies;
using Insys.Life.Domain.Proposals;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

/// <summary>Command IssuePolicy from the accepted proposal, billed on <paramref name="BillingAccountId"/> (the payer's account) or a new account Billing opens.</summary>
public sealed record IssueLifePolicy(Guid ProposalId, Guid? BillingAccountId) : ICommand<PolicyView>;

public sealed record GetLifePolicy(Guid PolicyId) : IQuery<PolicyView>;

public sealed record ChangeBeneficiaries(Guid PolicyId, DateOnly EffectiveDate, IReadOnlyList<BeneficiaryNomination> Nominations) : ICommand<PolicyView>;

public sealed record AlterLifePolicy(Guid PolicyId, string AlterationCode, DateOnly EffectiveDate, string? IntermediaryCode) : ICommand<PolicyView>;

public sealed record ReinstateLifePolicy(Guid PolicyId, Guid? EvidenceRef) : ICommand<PolicyView>;

public enum PolicyValueAction
{
    Surrender,
    MakePaidUp,
    Mature,
}

/// <summary>Commands SurrenderPolicy, MakePaidUp and MaturePolicy. TODO(spec): their values need actuarial calculations (OQ-052); they are refused until then.</summary>
public sealed record RequestPolicyValue(Guid PolicyId, PolicyValueAction Action) : ICommand<PolicyView>;

public sealed record IssuePolicyLoan(Guid PolicyId, decimal Principal, string RateCode) : ICommand<LoanView>;

public sealed record RepayPolicyLoan(Guid LoanId, decimal Amount, Guid? PaymentRef) : ICommand<LoanView>;

public sealed record GetPolicyLoan(Guid LoanId) : IQuery<LoanView>;

public sealed class ChangeBeneficiariesValidator : AbstractValidator<ChangeBeneficiaries>
{
    public ChangeBeneficiariesValidator() =>
        RuleForEach(c => c.Nominations).ChildRules(n =>
        {
            n.RuleFor(x => x.PartyId).NotEmpty();
            n.RuleFor(x => x.RelationshipCode).NotEmpty().MaximumLength(15);
            n.RuleFor(x => x.SharePct).PrecisionScale(5, 2, true);
        });
}

public sealed class AlterLifePolicyValidator : AbstractValidator<AlterLifePolicy>
{
    public AlterLifePolicyValidator()
    {
        RuleFor(c => c.AlterationCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.IntermediaryCode).MaximumLength(15);
    }
}

public sealed class IssuePolicyLoanValidator : AbstractValidator<IssuePolicyLoan>
{
    public IssuePolicyLoanValidator()
    {
        RuleFor(c => c.Principal).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.RateCode).NotEmpty().MaximumLength(15);
    }
}

public sealed class RepayPolicyLoanValidator : AbstractValidator<RepayPolicyLoan>
{
    public RepayPolicyLoanValidator() => RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
}

internal static class LifePolicies
{
    public static async Task<Result<LifePolicy>> Load(ILifeRepository life, Guid id, CancellationToken cancellationToken) =>
        await life.Policy(id, cancellationToken) is { } policy ? policy : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
}

internal sealed class IssueLifePolicyHandler(ILifeRepository life, ILifeCodes codes, ISellableProducts products, ITenantContext tenant, IClock clock, IAuditTrail audit)
    : ICommandHandler<IssueLifePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(IssueLifePolicy command, CancellationToken cancellationToken)
    {
        if (await life.Proposal(command.ProposalId, cancellationToken) is not { } proposal)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The proposal was not found.");
        }

        if (await life.PolicyForProposal(proposal.Id, cancellationToken) is { } existing)
        {
            return PolicyView.From(existing);
        }

        if (await life.CaseForProposal(proposal.Id, cancellationToken) is not { } decided)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The proposal has not been underwritten.");
        }

        // Issued on the approved product version (LIF-003), still on sale when the policy is issued.
        var product = await Products.Sellable(products, tenant.RequireTenantId(), proposal.ProductCode, proposal.ProductVersion, clock.UtcToday, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        var id = Guid.CreateVersion7();
        var number = await Numbers.Policy(codes, id, clock.UtcToday, cancellationToken);
        if (number.IsFailure)
        {
            return number.Error;
        }

        var policy = LifePolicy.IssuePolicy(id, number.Value, proposal, decided, command.BillingAccountId ?? Guid.CreateVersion7(), clock.UtcToday, clock.UtcNow);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        life.Add(policy.Value);
        audit.Record(new AuditEntry("LIFE", "IssuePolicy", nameof(LifePolicy), id.ToString(), new Dictionary<string, string?> { ["proposal"] = proposal.Id.ToString() }));
        return PolicyView.From(policy.Value);
    }
}

internal sealed class GetLifePolicyHandler(ILifeRepository life) : IQueryHandler<GetLifePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(GetLifePolicy query, CancellationToken cancellationToken)
    {
        var policy = await LifePolicies.Load(life, query.PolicyId, cancellationToken);
        return policy.IsFailure ? policy.Error : PolicyView.From(policy.Value);
    }
}

internal sealed class ChangeBeneficiariesHandler(ILifeRepository life, ILifeCodes codes, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ChangeBeneficiaries, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(ChangeBeneficiaries command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        if (user.IsFailure || policy.IsFailure)
        {
            return user.IsFailure ? user.Error : policy.Error!;
        }

        foreach (var relationship in command.Nominations.Select(n => n.RelationshipCode).Distinct())
        {
            if (!await codes.Exists(LifeCode.BeneficiaryRelationship, relationship, clock.UtcToday, cancellationToken))
            {
                return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{relationship} is not a beneficiary relationship in force.");
            }
        }

        audit.Record(new AuditEntry("LIFE", "ChangeBeneficiaries", nameof(LifePolicy), policy.Value.Id.ToString()));
        var result = policy.Value.ChangeBeneficiaries(command.Nominations, command.EffectiveDate,
            await codes.NominationRule(policy.Value.ProductCode, clock.UtcToday, cancellationToken), user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy.Value);
    }
}

/// <summary>An alteration is applied only by a user holding the alteration type's authority role (life.cfg_alteration_types.authority_role).</summary>
internal sealed class AlterLifePolicyHandler(ILifeRepository life, ILifeCodes codes, IUserRoleQuery roles, ITenantContext tenant, IExecutionContext execution,
    IClock clock, IAuditTrail audit)
    : ICommandHandler<AlterLifePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(AlterLifePolicy command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        if (user.IsFailure || policy.IsFailure)
        {
            return user.IsFailure ? user.Error : policy.Error!;
        }

        if (await codes.AlterationType(command.AlterationCode, clock.UtcToday, cancellationToken) is not { } alteration)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{command.AlterationCode} is not an alteration type in force.");
        }

        if (!string.IsNullOrEmpty(alteration.AuthorityRole)
            && !(await roles.EffectiveRoles(tenant.RequireTenantId(), user.Value, cancellationToken)).Contains(alteration.AuthorityRole))
        {
            return Error.Forbidden(LifeErrorCodes.AuthorityMissing, $"{alteration.Code} is applied by a user holding the {alteration.AuthorityRole} role.");
        }

        audit.Record(new AuditEntry("LIFE", "AlterPolicy", nameof(LifePolicy), policy.Value.Id.ToString(),
            new Dictionary<string, string?> { ["alteration"] = alteration.Code }));
        var result = policy.Value.AlterPolicy(alteration, command.IntermediaryCode, command.EffectiveDate, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy.Value);
    }
}

/// <summary>Arrears are checked only when the product version requires them to be paid first (reinstatement_arrears REQUIRED).</summary>
internal sealed class ReinstateLifePolicyHandler(ILifeRepository life, ILifeCodes codes, ISellableProducts products, IPolicyArrears arrears, ITenantContext tenant,
    IExecutionContext execution, IClock clock, IAuditTrail audit)
    : ICommandHandler<ReinstateLifePolicy, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(ReinstateLifePolicy command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        if (user.IsFailure || policy.IsFailure)
        {
            return user.IsFailure ? user.Error : policy.Error!;
        }

        if (await codes.LapseRule(policy.Value.ProductCode, clock.UtcToday, cancellationToken) is not { } rule)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{policy.Value.ProductCode} has no lapse and reinstatement rule in force.");
        }

        var tenantId = tenant.RequireTenantId();
        var product = await products.Version(tenantId, policy.Value.ProductCode, policy.Value.ProductVersion, clock.UtcToday, cancellationToken);
        var unpaid = product?.ReinstatementArrears == "REQUIRED"
            ? (await arrears.For(tenantId, LifeSubjects.SourceDomain, policy.Value.Id, clock.UtcToday, cancellationToken)).OverdueInstalments
            : 0;
        audit.Record(new AuditEntry("LIFE", "ReinstatePolicy", nameof(LifePolicy), policy.Value.Id.ToString()));
        var result = policy.Value.ReinstatePolicy(rule, clock.UtcToday, command.EvidenceRef, unpaid, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : PolicyView.From(policy.Value);
    }
}

internal sealed class RequestPolicyValueHandler(ILifeRepository life) : ICommandHandler<RequestPolicyValue, PolicyView>
{
    public async Task<Result<PolicyView>> Handle(RequestPolicyValue command, CancellationToken cancellationToken)
    {
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        return policy.IsFailure
            ? policy.Error
            : Error.Conflict(LifeErrorCodes.CalculationNotConfigured,
                $"{command.Action} needs a value calculation (surrender value scales, paid-up and maturity values) that actuarial has not defined (OQ-052).");
    }
}

internal sealed class IssuePolicyLoanHandler(ILifeRepository life, ILifeCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<IssuePolicyLoan, LoanView>
{
    public async Task<Result<LoanView>> Handle(IssuePolicyLoan command, CancellationToken cancellationToken)
    {
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        if (!await codes.Exists(LifeCode.LoanRate, command.RateCode, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{command.RateCode} is not a policy loan rate in force.");
        }

        var outstanding = (await life.ActiveLoans(policy.Value.Id, cancellationToken)).Sum(l => l.Balance);
        var sequence = await life.LoanCount(policy.Value.Id, cancellationToken) + 1;
        var id = Guid.CreateVersion7();
        var loan = LifePolicyLoan.IssueLoan(id, $"{policy.Value.PolicyNumber[..Math.Min(12, policy.Value.PolicyNumber.Length)]}L{sequence:D2}", policy.Value,
            await codes.LoanRule(policy.Value.ProductCode, clock.UtcToday, cancellationToken), await life.CashValue(policy.Value.Id, clock.UtcToday, cancellationToken),
            outstanding, command.Principal, command.RateCode, clock.UtcToday, clock.UtcNow);
        if (loan.IsFailure)
        {
            return loan.Error;
        }

        life.Add(loan.Value);
        audit.Record(new AuditEntry("LIFE", "IssueLoan", nameof(LifePolicyLoan), id.ToString(), new Dictionary<string, string?> { ["policy"] = policy.Value.Id.ToString() }));
        return LoanView.From(loan.Value);
    }
}

internal sealed class RepayPolicyLoanHandler(ILifeRepository life, IClock clock, IAuditTrail audit) : ICommandHandler<RepayPolicyLoan, LoanView>
{
    public async Task<Result<LoanView>> Handle(RepayPolicyLoan command, CancellationToken cancellationToken)
    {
        if (await life.Loan(command.LoanId, cancellationToken) is not { } loan)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The loan was not found.");
        }

        audit.Record(new AuditEntry("LIFE", "RecordRepayment", nameof(LifePolicyLoan), loan.Id.ToString()));
        var result = loan.RecordRepayment(command.Amount, clock.UtcToday, command.PaymentRef, clock.UtcNow);
        return result.IsFailure ? result.Error : LoanView.From(loan);
    }
}

internal sealed class GetPolicyLoanHandler(ILifeRepository life) : IQueryHandler<GetPolicyLoan, LoanView>
{
    public async Task<Result<LoanView>> Handle(GetPolicyLoan query, CancellationToken cancellationToken) =>
        await life.Loan(query.LoanId, cancellationToken) is { } loan ? LoanView.From(loan) : Error.NotFound(ErrorCodes.NotFound, "The loan was not found.");
}
