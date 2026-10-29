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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.Life.Domain;
using Insys.Life.Domain.Claims;
using Insys.Life.Domain.Policies;
using Insys.Party.Contracts;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

/// <summary>Command NotifyClaim: an event on an insured life of the policy, by a claimant.</summary>
public sealed record NotifyLifeClaim(Guid PolicyId, [property: BuildingBlocks.Domain.CodeList(LifeCodeLists.ClaimTypes)] string ClaimType, DateOnly EventDate,
    [property: BuildingBlocks.Domain.CodeList(LifeCodeLists.CausesOfEvent)] string? CauseCode, Guid ClaimantPartyId, Guid InsuredLifeId)
    : ICommand<LifeClaimView>;

public sealed record RegisterLifeClaim(Guid ClaimId) : ICommand<LifeClaimView>;

public sealed record AssessLifeClaim(Guid ClaimId, IReadOnlyList<BenefitLineRequest> Lines) : ICommand<LifeClaimView>;

public sealed record PayeeInput(Guid PartyId, Guid BankAccountId, decimal SharePct);

/// <summary>Command ApproveClaim, as the maker: payees and shares, the reserve paid from, the reason and rationale.</summary>
public sealed record ApproveLifeClaim(Guid ClaimId, IReadOnlyList<PayeeInput> Payees, string ReserveType, string DecisionReason, string Rationale)
    : ICommand<LifeClaimView>;

public sealed record DeclineLifeClaim(Guid ClaimId, string DecisionReason, string Rationale) : ICommand<LifeClaimView>;

public sealed record CloseLifeClaim(Guid ClaimId) : ICommand<LifeClaimView>;

public sealed record GetLifeClaim(Guid ClaimId) : IQuery<LifeClaimView>;

public sealed class NotifyLifeClaimValidator : AbstractValidator<NotifyLifeClaim>
{
    public NotifyLifeClaimValidator()
    {
        RuleFor(c => c.ClaimType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.CauseCode).MaximumLength(10);
        RuleFor(c => c.ClaimantPartyId).NotEmpty();
    }
}

public sealed class ApproveLifeClaimValidator : AbstractValidator<ApproveLifeClaim>
{
    public ApproveLifeClaimValidator()
    {
        RuleFor(c => c.Payees).NotEmpty();
        RuleFor(c => c.ReserveType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.DecisionReason).NotEmpty().MaximumLength(20);
        RuleForEach(c => c.Payees).ChildRules(p => p.RuleFor(x => x.SharePct).PrecisionScale(5, 2, true));
    }
}

public sealed class DeclineLifeClaimValidator : AbstractValidator<DeclineLifeClaim>
{
    public DeclineLifeClaimValidator() => RuleFor(c => c.DecisionReason).NotEmpty().MaximumLength(20);
}

internal static class LifeClaims
{
    public static async Task<Result<(LifeClaim Claim, LifePolicy Policy)>> Load(ILifeRepository life, Guid claimId, CancellationToken cancellationToken)
    {
        if (await life.Claim(claimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        return await life.Policy(claim.PolicyId, cancellationToken) is { } policy ? (claim, policy) : Error.NotFound(ErrorCodes.NotFound, "The policy was not found.");
    }

    /// <summary>The policyholder, the insured life, or a beneficiary on the event date.</summary>
    public static bool Entitled(LifePolicy policy, LifeClaim claim, Guid partyId) =>
        partyId == policy.PolicyholderPartyId || policy.Lives.Any(l => l.Id == claim.InsuredLifeId && l.PartyId == partyId)
        || policy.BeneficiariesOn(claim.EventDate).Any(b => b.PartyId == partyId);
}

internal sealed class NotifyLifeClaimHandler(ILifeRepository life, ILifeCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit, IExecutionContext execution)
    : ICommandHandler<NotifyLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(NotifyLifeClaim command, CancellationToken cancellationToken)
    {
        var policy = await LifePolicies.Load(life, command.PolicyId, cancellationToken);
        if (policy.IsFailure)
        {
            return policy.Error;
        }

        if (await codes.ClaimBenefitType(command.ClaimType, businessDate.Today, cancellationToken) is null)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{command.ClaimType} is not a Life claim type in force.");
        }

        if (command.CauseCode is { } cause && !await codes.Exists(LifeCode.CauseOfEvent, cause, businessDate.Today, cancellationToken))
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{cause} is not a cause of event in force.");
        }

        var claim = LifeClaim.NotifyClaim(Guid.CreateVersion7(), policy.Value, command.ClaimType, command.EventDate, command.CauseCode, command.ClaimantPartyId,
            command.InsuredLifeId, businessDate.Today, clock.UtcNow);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        claim.Value.RecordChannel(execution.ChannelCode);
        life.Add(claim.Value);
        audit.Record(new AuditEntry("LIFE", "NotifyClaim", nameof(LifeClaim), claim.Value.Id.ToString()));
        return LifeClaimView.From(claim.Value);
    }
}

/// <summary>
/// Command RegisterClaim (LIF-008): the policy was in force with an active benefit of the claim type's benefit type on
/// the insured life, no exclusion of that benefit covers the cause, the claimant is entitled, and no other claim is
/// open for the same benefit and event (CLM-005). Then the Claims Framework allocates the number.
/// </summary>
internal sealed class RegisterLifeClaimHandler(ILifeRepository life, ILifeCodes codes, IExclusionCodes exclusions, IClaimRegistry registry, ITenantContext tenant,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit, Microsoft.Extensions.Options.IOptions<LifeClaimOptions> options) : ICommandHandler<RegisterLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(RegisterLifeClaim command, CancellationToken cancellationToken)
    {
        var loaded = await LifeClaims.Load(life, command.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (claim, policy) = loaded.Value;
        if (claim.Status != LifeClaimStatus.Notified)
        {
            return LifeClaimView.From(claim);
        }

        if (await codes.ClaimBenefitType(claim.ClaimType, businessDate.Today, cancellationToken) is not { } benefitType)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{claim.ClaimType} is not a Life claim type in force.");
        }

        var benefit = policy.CoversEvent(claim.InsuredLifeId, benefitType, claim.EventDate,
            await codes.GraceRule(policy.PremiumFrequency, businessDate.Today, cancellationToken));
        if (benefit.IsFailure)
        {
            return benefit.Error;
        }

        if (benefit.Value.ExclusionCodes is { Length: > 0 } excluded && claim.CauseCode is { } cause
            && (await exclusions.InForce(tenant.RequireTenantId(), excluded, businessDate.Today, cancellationToken)).FirstOrDefault(e => CodeRanges.Contains(e.DiagnosisCodes, cause)) is { } exclusion)
        {
            return Error.Validation(LifeErrorCodes.NotCovered, $"The cause is excluded from the benefit ({exclusion.Code}).");
        }

        if (!LifeClaims.Entitled(policy, claim, claim.ClaimantPartyId))
        {
            return Error.Validation(LifeErrorCodes.ClaimantNotAuthorised, "The claimant is not the policyholder, the insured life or a beneficiary.");
        }

        if (await life.OpenClaimExists(policy.Id, claim.ClaimType, claim.EventDate, claim.Id, cancellationToken))
        {
            return Error.Conflict(LifeErrorCodes.ClaimAlreadyOpen, "A claim for this benefit and event is already open (CLM-005).");
        }

        var facts = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["days_since_inception"] = claim.EventDate.DayNumber - policy.InceptionDate.DayNumber,
            ["days_to_notification"] = businessDate.DateOf(claim.NotifiedAt).DayNumber - claim.EventDate.DayNumber,
            ["claim_type"] = claim.ClaimType,
            ["cause_code"] = claim.CauseCode,
            ["sum_assured"] = benefit.Value.SumAssured,
        });
        // OQ-079 (decided 24 October 2026): a lump-sum claim (death, or disability where the benefit is a fixed sum) starts
        // with a reserve equal to the sum assured of each claimed cover: the insured life's benefits of that type in force on
        // the event date. The assessor adjusts it with a reason. An income benefit starts with none.
        InitialReserve? reserve = null;
        if (await codes.PaysLumpSum(benefitType, businessDate.Today, cancellationToken))
        {
            var sumAssured = policy.Benefits.Where(b => b.InsuredLifeId == claim.InsuredLifeId && b.BenefitCode == benefitType && b.CoversOn(claim.EventDate))
                .Sum(b => b.SumAssured);
            reserve = new InitialReserve(options.Value.InitialReserveType, sumAssured, claim.Currency, $"Sum assured of the claimed {benefitType} cover (OQ-079)");
        }

        var registered = await registry.Register(LifeSubjectsDomain, claim.Id, policy.Id, new ClaimOwner(policy.ProductCode, policy.OrgNodeCode), facts, reserve, cancellationToken);
        if (!registered.Succeeded)
        {
            return Error.Validation(registered.ErrorCode ?? ErrorCodes.ValidationFailed, registered.ErrorMessage ?? "The claim number could not be allocated.");
        }

        audit.Record(new AuditEntry("LIFE", "RegisterClaim", nameof(LifeClaim), claim.Id.ToString(), new Dictionary<string, string?> { ["claim"] = registered.ClaimNumber }));
        var result = claim.RegisterClaim(registered.ClaimNumber!, clock.UtcNow);
        return result.IsFailure ? result.Error : LifeClaimView.From(claim);
    }

    private const string LifeSubjectsDomain = Contracts.LifeSubjects.SourceDomain;
}

public sealed class LifeClaimOptions
{
    /// <summary>
    /// The Claims Framework reserve type a lump-sum claim's initial reserve is set on (OQ-079); approval then pays from it.
    /// TODO(spec): the reserve type for Life benefits is not specified; INDEMNITY is the type the claims reserve types hold.
    /// </summary>
    public string InitialReserveType { get; set; } = "INDEMNITY";
}

internal sealed class AssessLifeClaimHandler(ILifeRepository life, IAuditTrail audit) : ICommandHandler<AssessLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(AssessLifeClaim command, CancellationToken cancellationToken)
    {
        var loaded = await LifeClaims.Load(life, command.ClaimId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (claim, policy) = loaded.Value;
        // Only benefits on the claim's insured life that covered the event date are claimed.
        var covered = policy.Benefits.Where(b => b.InsuredLifeId == claim.InsuredLifeId && b.CoversOn(claim.EventDate)).ToDictionary(b => b.Id, b => b.SumAssured);
        audit.Record(new AuditEntry("LIFE", "AssessClaim", nameof(LifeClaim), claim.Id.ToString()));
        var result = claim.AssessClaim(command.Lines, covered);
        return result.IsFailure ? result.Error : LifeClaimView.From(claim);
    }
}

internal sealed class ApproveLifeClaimHandler(ILifeRepository life, IBankAccounts bankAccounts, IClaimDecisionReasons reasons, ITenantContext tenant, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<ApproveLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(ApproveLifeClaim command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var loaded = await LifeClaims.Load(life, command.ClaimId, cancellationToken);
        if (user.IsFailure || loaded.IsFailure)
        {
            return user.IsFailure ? user.Error : loaded.Error!;
        }

        var (claim, policy) = loaded.Value;
        var payees = new List<PayeeRequest>();
        foreach (var payee in command.Payees)
        {
            if (!LifeClaims.Entitled(policy, claim, payee.PartyId))
            {
                return Error.Validation(LifeErrorCodes.PayeeInvalid, $"Party {payee.PartyId} is not the policyholder, the insured life or a beneficiary.");
            }

            // Paid only to the payee's own verified, current account in the policy's currency (ADR-013).
            if (await bankAccounts.Get(tenant.RequireTenantId(), payee.BankAccountId, cancellationToken) is not { IsPayable: true } account
                || account.PartyId != payee.PartyId || account.Currency != policy.Currency)
            {
                return Error.Validation(LifeErrorCodes.BankAccountNotPayable, "A bank account is not the payee's verified, current account in the policy's currency.");
            }

            payees.Add(new PayeeRequest(payee.PartyId, account.Id, account.AccountNumberHash, payee.SharePct));
        }

        // A policy loan is settled from the claim first (LifePolicyLoan: OffsetAgainstBenefit).
        var loanOffset = (await life.ActiveLoans(policy.Id, cancellationToken)).Sum(l => l.Balance);
        audit.Record(new AuditEntry("LIFE", "ApproveClaim", nameof(LifeClaim), claim.Id.ToString()));
        // The decision reason as a claim decision reason (OQ-102): an approval below the amount claimed needs a PARTIAL one.
        var reason = await reasons.InForce(tenant.RequireTenantId(), command.DecisionReason, businessDate.Today, cancellationToken);
        var result = claim.RequestApproval(payees, loanOffset, command.ReserveType, command.DecisionReason, command.Rationale, user.Value, clock.UtcNow,
            reason?.DecisionType);
        return result.IsFailure ? result.Error : LifeClaimView.From(claim);
    }
}

internal sealed class DeclineLifeClaimHandler(ILifeRepository life, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<DeclineLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(DeclineLifeClaim command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await life.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        audit.Record(new AuditEntry("LIFE", "DeclineClaim", nameof(LifeClaim), claim.Id.ToString()));
        var result = claim.DeclineClaim(command.DecisionReason, command.Rationale, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : LifeClaimView.From(claim);
    }
}

internal sealed class CloseLifeClaimHandler(ILifeRepository life) : ICommandHandler<CloseLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(CloseLifeClaim command, CancellationToken cancellationToken)
    {
        if (await life.Claim(command.ClaimId, cancellationToken) is not { } claim)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
        }

        var result = claim.CloseClaim();
        return result.IsFailure ? result.Error : LifeClaimView.From(claim);
    }
}

internal sealed class GetLifeClaimHandler(ILifeRepository life, Workflow.Contracts.IApprovalHistory history) : IQueryHandler<GetLifeClaim, LifeClaimView>
{
    public async Task<Result<LifeClaimView>> Handle(GetLifeClaim query, CancellationToken cancellationToken) =>
        await life.Claim(query.ClaimId, cancellationToken) is { } claim
            ? LifeClaimView.From(claim) with
            {
                LatestRejection = await history.Latest(Contracts.LifeApprovals.ClaimBusinessRefType, claim.Id, cancellationToken),
                ApprovalId = claim.ApprovalId ?? await history.Waiting(Contracts.LifeApprovals.ClaimBusinessRefType, claim.Id, cancellationToken),
            }
            : Error.NotFound(ErrorCodes.NotFound, "The claim was not found.");
}
