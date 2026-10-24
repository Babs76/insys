/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.ClaimsFramework.Domain.Claims;
using Insys.General.Contracts;
using Insys.Life.Contracts;
using Insys.Party.Contracts;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.Logging;

namespace Insys.ClaimsFramework.Application;

public interface IClaimsRepository
{
    Task<ClaimRegistration?> ByNumber(string claimNumber, CancellationToken cancellationToken);

    Task<ClaimRegistration?> ByDomainClaim(string domain, Guid domainClaimId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialMovement>> Movements(string claimNumber, CancellationToken cancellationToken);

    Task<ClaimPaymentInstruction?> Instruction(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ClaimPaymentInstruction>> Instructions(string claimNumber, CancellationToken cancellationToken);

    Task<ClaimPaymentInstruction?> Instruction(string claimNumber, string instructionRef, CancellationToken cancellationToken);

    void Add(ClaimRegistration registration);

    void Add(FinancialMovement movement);

    void Add(ClaimPaymentInstruction instruction);
}

/// <summary>The Claims module's static data in force.</summary>
public interface IClaimsCodes
{
    /// <summary>The claim number prefix for a domain (claims.cfg_claim_numbering_schemes).</summary>
    Task<string?> NumberPrefix(string domain, DateOnly day, CancellationToken cancellationToken);

    Task<bool> EvidenceTypeExists(string code, DateOnly day, CancellationToken cancellationToken);

    Task<bool> ReserveTypeExists(string code, DateOnly day, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<FraudIndicator>>> FraudIndicators(DateOnly day, CancellationToken cancellationToken);
}

public sealed record EvidenceView(string EvidenceType, [property: OneOf("REQUESTED", "RECEIVED")] string Status, Guid? DocumentId, DateTimeOffset? RequestedAt, DateTimeOffset? ReceivedAt);

public sealed record FraudHitView(string IndicatorCode, int Score, bool Referred, DateTimeOffset EvaluatedAt);

/// <summary>An indicator a screening skipped (F009): the facts it lacked, or the evaluation's error code.</summary>
public sealed record SkippedIndicatorView(string IndicatorCode, [property: OneOf(SkipReasons.MissingFacts, SkipReasons.EvaluationFailed)] string Reason,
    IReadOnlyList<string> MissingFacts, string? ErrorCode);

/// <summary>One screening of the claim against the fraud indicators in force, with what it skipped and why (F009).</summary>
public sealed record FraudEvaluationView(Guid Id, DateTimeOffset EvaluatedAt, int Score, bool Referred, int IndicatorsEvaluated, IReadOnlyList<SkippedIndicatorView> Skipped);

public sealed record ClaimView(Guid Id, string ClaimNumber, string Domain, Guid DomainClaimId, Guid PolicyRef, DateTimeOffset RegisteredAt,
    IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<FraudHitView> FraudHits, int Communications, IReadOnlyList<FraudEvaluationView> FraudEvaluations)
{
    public static ClaimView From(ClaimRegistration c) => new(c.Id, c.ClaimNumber, c.Domain, c.DomainClaimId, c.PolicyRef, c.RegisteredAt,
        c.Evidence.Select(e => new EvidenceView(e.EvidenceType, e.Status, e.DocumentId, e.RequestedAt, e.ReceivedAt)).ToList(),
        c.FraudHits.Select(h => new FraudHitView(h.IndicatorCode, h.Score, h.Referred, h.EvaluatedAt)).ToList(), c.Communications.Count,
        c.FraudEvaluations.OrderBy(e => e.EvaluatedAt).Select(e => new FraudEvaluationView(e.Id, e.EvaluatedAt, e.Score, e.Referred, e.IndicatorsEvaluated,
            e.Skipped.Select(s => new SkippedIndicatorView(s.IndicatorCode, s.Reason, s.MissingFacts, s.ErrorCode)).ToList())).ToList());
}

public sealed record MovementView(Guid Id, string MovementType, string ReserveType, decimal Amount, string Currency, DateTimeOffset MovementAt);

public sealed record FinancialsView(string ClaimNumber, IReadOnlyList<MovementView> Movements, IReadOnlyDictionary<string, decimal> OutstandingReserves)
{
    public static FinancialsView From(ClaimFinancials f) => new(f.ClaimNumber,
        f.Movements.OrderBy(m => m.MovementAt).ThenBy(m => m.Id).Select(m => new MovementView(m.Id, m.MovementType.ToString(), m.ReserveType, m.Amount, m.Currency, m.MovementAt)).ToList(),
        f.Movements.Select(m => m.ReserveType).Distinct().ToDictionary(t => t, f.Outstanding));
}

public sealed record InstructionView(Guid Id, string ClaimNumber, string InstructionRef, Guid PayeePartyId, decimal Amount, decimal ApprovedAmount, string Currency,
    string ReserveType, InstructionStatus Status, Guid? ApprovalId, Guid? PaymentId, int Attempt, Guid? BankAccountId, string? PayeeBankHash, Guid? PendingPayeePartyId,
    Guid? PendingBankAccountId,
    [property: OneOf("RESERVE_BELOW_ZERO", "PAYEE_ACCOUNT_CHANGED")] string? HeldReason = null, string? HeldDetail = null, DateTimeOffset? HeldAt = null)
{
    public static InstructionView From(ClaimPaymentInstruction i) => new(i.Id, i.ClaimNumber, i.InstructionRef, i.PayeePartyId, i.Amount, i.ApprovedAmount, i.Currency,
        i.ReserveType, i.Status, i.ApprovalId, i.PaymentId, i.Attempt, i.BankAccountId, i.PayeeBankHash, i.PendingPayeePartyId, i.PendingBankAccountId, i.HeldReason,
        i.HeldDetail, i.HeldAt);
}

/// <summary>Command AllocateClaimNumber; a repeat for the same domain claim returns the same number (CLM-005).</summary>
/// <param name="ProductCode">The domain claim's product, and <paramref name="OrgNodeCode"/> its organisation node: they scope the claim's records (F103).</param>
public sealed record AllocateClaimNumber(string Domain, Guid DomainClaimId, Guid PolicyRef, string? ProductCode = null, string? OrgNodeCode = null) : ICommand<ClaimView>;

public sealed record RequestEvidence(string ClaimNumber, string EvidenceType) : ICommand<ClaimView>;

public sealed record RecordEvidence(string ClaimNumber, string EvidenceType, Guid DocumentId) : ICommand<ClaimView>;

public sealed record RecordCommunication(string ClaimNumber, string Direction, string Channel, Guid? DocumentId) : ICommand<ClaimView>;

public sealed record EvaluateFraudIndicators(string ClaimNumber, JsonElement Facts) : ICommand<ClaimView>;

public enum ReserveAction
{
    Set,
    Change,
    Recovery,
}

/// <summary>Commands SetReserve, ChangeReserve and RecordRecovery (CLM-004).</summary>
/// <param name="Reason">Why the reserve moves; recorded in the audit trail. The initial reserve gives its basis (OQ-079).</param>
public sealed record MoveReserve(string ClaimNumber, ReserveAction Action, string ReserveType, decimal Amount, string Currency, string? Reason = null)
    : ICommand<FinancialsView>;

public sealed class ClaimsPaymentOptions
{
    /// <summary>
    /// The role whose queue gets the task when an approved instruction cannot be released automatically. The catalogue's
    /// sample claims roles are CLAIMS_ASSESSOR and CLAIMS_MANAGER; TODO(spec): which role resolves a held payment.
    /// </summary>
    public string HeldReleaseRole { get; set; } = "CLAIMS_MANAGER";
}

/// <summary>Command DraftInstruction; a repeat for the same claim and reference returns the same instruction (PAY-001).</summary>
/// <param name="BankAccountId">The payee's verified bank account (party.bank_account); the approval covers it.</param>
public sealed record DraftInstruction(string ClaimNumber, string InstructionRef, Guid PayeePartyId, Guid BankAccountId, decimal Amount, decimal ApprovedAmount,
    string Currency, string ReserveType) : ICommand<InstructionView>;

public enum InstructionAction
{
    Submit,
    Release,
}

public sealed record ChangeInstruction(Guid InstructionId, InstructionAction Action) : ICommand<InstructionView>;

/// <summary>Releases a failed payment again; a different payee or bank account waits for a second person's approval first.</summary>
public sealed record RetryInstruction(Guid InstructionId, Guid PayeePartyId, Guid BankAccountId) : ICommand<InstructionView>;

/// <summary>Redirects an approved instruction, not yet released, to another payee or bank account; a second person approves it first.</summary>
public sealed record RedirectInstruction(Guid InstructionId, Guid PayeePartyId, Guid BankAccountId) : ICommand<InstructionView>;

public sealed record GetClaim(string ClaimNumber) : IQuery<ClaimView>;

public sealed record GetFinancials(string ClaimNumber) : IQuery<FinancialsView>;

public sealed record GetInstruction(Guid InstructionId) : IQuery<InstructionView>;

/// <summary>GAP-036: a claim's payment instructions, oldest first.</summary>
public sealed record ListInstructions(string ClaimNumber) : IQuery<IReadOnlyList<InstructionView>>;

public sealed class AllocateClaimNumberValidator : AbstractValidator<AllocateClaimNumber>
{
    public AllocateClaimNumberValidator()
    {
        RuleFor(c => c.Domain).Must(d => d is "LIFE" or "GENERAL" or "HEALTH").WithMessage("The domain is LIFE, GENERAL or HEALTH.");
        RuleFor(c => c.DomainClaimId).NotEmpty();
        RuleFor(c => c.PolicyRef).NotEmpty();
    }
}

public sealed class MoveReserveValidator : AbstractValidator<MoveReserve>
{
    public MoveReserveValidator()
    {
        RuleFor(c => c.ReserveType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Amount).PrecisionScale(18, 2, true);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500).WithMessage("A reserve moves with a reason (OQ-079).");
    }
}

public sealed class DraftInstructionValidator : AbstractValidator<DraftInstruction>
{
    public DraftInstructionValidator()
    {
        RuleFor(c => c.InstructionRef).NotEmpty().MaximumLength(60);
        RuleFor(c => c.PayeePartyId).NotEmpty();
        RuleFor(c => c.Amount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.ApprovedAmount).GreaterThan(0).PrecisionScale(18, 2, true);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.ReserveType).NotEmpty().MaximumLength(15);
    }
}

public sealed class RecordCommunicationValidator : AbstractValidator<RecordCommunication>
{
    public RecordCommunicationValidator()
    {
        RuleFor(c => c.Direction).Must(d => d is "IN" or "OUT").WithMessage("The direction is IN or OUT.");
        RuleFor(c => c.Channel).NotEmpty().MaximumLength(10);
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

internal static class Loaded
{
    public static async Task<Result<ClaimRegistration>> Claim(IClaimsRepository claims, string number, CancellationToken cancellationToken) =>
        await claims.ByNumber(number, cancellationToken) is { } claim ? claim : Error.NotFound(ClaimsErrorCodes.ClaimNotRegistered, $"Claim {number} is not registered.");

    public static async Task<ClaimFinancials> Financials(IClaimsRepository claims, ClaimRegistration claim, string currency, CancellationToken cancellationToken) =>
        new(claim.ClaimNumber, claim.Domain, currency, await claims.Movements(claim.ClaimNumber, cancellationToken));
}

internal sealed class AllocateClaimNumberHandler(IClaimsRepository claims, IClaimsCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<AllocateClaimNumber, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(AllocateClaimNumber command, CancellationToken cancellationToken)
    {
        if (await claims.ByDomainClaim(command.Domain, command.DomainClaimId, cancellationToken) is { } existing)
        {
            return ClaimView.From(existing);
        }

        var prefix = await codes.NumberPrefix(command.Domain, businessDate.Today, cancellationToken) ?? command.Domain[..3];
        var claim = ClaimRegistration.AllocateClaimNumber(Guid.CreateVersion7(), prefix, command.Domain, command.DomainClaimId, command.PolicyRef, clock.UtcNow,
            command.ProductCode, command.OrgNodeCode);
        claims.Add(claim);
        return ClaimView.From(claim);
    }
}

internal sealed class RequestEvidenceHandler(IClaimsRepository claims, IClaimsCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<RequestEvidence, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RequestEvidence command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.EvidenceTypeExists(command.EvidenceType, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.EvidenceType} is not an evidence type in force.");
        }

        var result = claim.Value.RequestEvidence(command.EvidenceType, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim.Value);
    }
}

internal sealed class RecordEvidenceHandler(IClaimsRepository claims, IClaimsCodes codes, IBusinessDate businessDate, IClock clock) : ICommandHandler<RecordEvidence, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RecordEvidence command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.EvidenceTypeExists(command.EvidenceType, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.EvidenceType} is not an evidence type in force.");
        }

        claim.Value.RecordEvidence(command.EvidenceType, command.DocumentId, clock.UtcNow);
        return ClaimView.From(claim.Value);
    }
}

internal sealed class RecordCommunicationHandler(IClaimsRepository claims, IClock clock) : ICommandHandler<RecordCommunication, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RecordCommunication command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        claim.Value.RecordCommunication(command.Direction, command.Channel, command.DocumentId, clock.UtcNow);
        return ClaimView.From(claim.Value);
    }
}

internal sealed class EvaluateFraudIndicatorsHandler(IClaimsRepository claims, IClaimsCodes codes, IBusinessDate businessDate, IClock clock, ILogger<EvaluateFraudIndicatorsHandler> logger)
    : ICommandHandler<EvaluateFraudIndicators, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(EvaluateFraudIndicators command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        var indicators = await codes.FraudIndicators(businessDate.Today, cancellationToken);
        if (indicators.IsFailure)
        {
            return indicators.Error;
        }

        var facts = command.Facts.ValueKind == JsonValueKind.Object
            ? command.Facts.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        var evaluation = claim.Value.EvaluateFraudIndicators(indicators.Value, facts, clock.UtcNow);
        if (evaluation.Skipped.Count > 0)
        {
            // Codes only: the facts may describe the claim. The evaluation records the skips, and FraudIndicatorsNotEvaluated tells the configuration owner.
            logger.LogWarning("Fraud indicators {Indicators} could not be evaluated on claim {ClaimNumber}", string.Join(", ", evaluation.NotEvaluated),
                command.ClaimNumber);
        }

        return ClaimView.From(claim.Value);
    }
}

internal sealed class MoveReserveHandler(IClaimsRepository claims, IClaimsCodes codes, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<MoveReserve, FinancialsView>
{
    public async Task<Result<FinancialsView>> Handle(MoveReserve command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.ReserveTypeExists(command.ReserveType, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.ReserveType} is not a reserve type in force.");
        }

        var financials = await Loaded.Financials(claims, claim.Value, command.Currency, cancellationToken);
        audit.Record(new AuditEntry("CLAIMS", $"Reserve{command.Action}", nameof(ClaimFinancials), claim.Value.Id.ToString(),
            new Dictionary<string, string?> { ["claim"] = command.ClaimNumber, ["reserveType"] = command.ReserveType, ["reason"] = command.Reason }));
        var movement = command.Action switch
        {
            ReserveAction.Set => financials.SetReserve(command.ReserveType, command.Amount, businessDate.Today, clock.UtcNow),
            ReserveAction.Change => financials.ChangeReserve(command.ReserveType, command.Amount, businessDate.Today, clock.UtcNow),
            _ => financials.RecordRecovery(command.ReserveType, command.Amount, businessDate.Today, clock.UtcNow),
        };
        if (movement.IsFailure)
        {
            return movement.Error;
        }

        claims.Add(movement.Value);
        return FinancialsView.From(financials);
    }
}

/// <summary>The payee's verified, current bank account in the currency, as an approval covers it.</summary>
internal static class Payees
{
    public static async Task<Result<PayeeAccount>> Account(IBankAccounts bankAccounts, ITenantContext tenant, Guid payee, Guid bankAccountId, string currency,
        CancellationToken cancellationToken) =>
        await bankAccounts.Get(tenant.RequireTenantId(), bankAccountId, cancellationToken) is { IsPayable: true } account && account.PartyId == payee
            && account.Currency == currency
            ? new PayeeAccount(payee, account.Id, account.AccountNumberHash)
            : Error.Validation(ClaimsErrorCodes.BankAccountChanged, "The bank account is not the payee's verified, current account in the payment's currency.");

    /// <summary>The approved account is still the payee's current one, with the same number: nothing changed since the approval.</summary>
    public static async Task<bool> Unchanged(IBankAccounts bankAccounts, ITenantContext tenant, PayeeAccount? approved, CancellationToken cancellationToken) =>
        approved is not null && await bankAccounts.Get(tenant.RequireTenantId(), approved.BankAccountId, cancellationToken) is { IsPayable: true } account
        && account.AccountNumberHash == approved.AccountHash;
}

internal sealed class DraftInstructionHandler(IClaimsRepository claims, IClaimsCodes codes, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution,
    IBusinessDate businessDate, IAuditTrail audit)
    : ICommandHandler<DraftInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(DraftInstruction command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (user.IsFailure || claim.IsFailure)
        {
            return user.IsFailure ? user.Error : claim.Error!;
        }

        if (await claims.Instruction(command.ClaimNumber, command.InstructionRef, cancellationToken) is { } existing)
        {
            return InstructionView.From(existing);
        }

        if (!await codes.ReserveTypeExists(command.ReserveType, businessDate.Today, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.ReserveType} is not a reserve type in force.");
        }

        var payee = await Payees.Account(bankAccounts, tenant, command.PayeePartyId, command.BankAccountId, command.Currency, cancellationToken);
        if (payee.IsFailure)
        {
            return payee.Error;
        }

        var instruction = ClaimPaymentInstruction.DraftInstruction(Guid.CreateVersion7(), claim.Value, command.InstructionRef, payee.Value, command.Amount,
            command.ApprovedAmount, command.Currency, command.ReserveType, user.Value);
        if (instruction.IsFailure)
        {
            return instruction.Error;
        }

        claims.Add(instruction.Value);
        audit.Record(new AuditEntry("CLAIMS", "DraftInstruction", nameof(ClaimPaymentInstruction), instruction.Value.Id.ToString()));
        return InstructionView.From(instruction.Value);
    }
}

internal sealed class ChangeInstructionHandler(IClaimsRepository claims, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution, IClock clock,
    IAuditTrail audit)
    : ICommandHandler<ChangeInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(ChangeInstruction command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await claims.Instruction(command.InstructionId, cancellationToken) is not { } instruction)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment instruction was not found.");
        }

        audit.Record(new AuditEntry("CLAIMS", $"{command.Action}Instruction", nameof(ClaimPaymentInstruction), instruction.Id.ToString()));
        Result result;
        if (command.Action == InstructionAction.Submit)
        {
            result = instruction.SubmitInstruction(user.Value, clock.UtcNow);
        }
        else
        {
            var claim = await Loaded.Claim(claims, instruction.ClaimNumber, cancellationToken);
            if (claim.IsFailure)
            {
                return claim.Error;
            }

            // A payee account superseded or changed since the approval needs a fresh approval (redirect the instruction).
            if (!await Payees.Unchanged(bankAccounts, tenant, instruction.Payee, cancellationToken))
            {
                return Error.Conflict(ClaimsErrorCodes.BankAccountChanged, "The payee's bank account changed after approval; redirect the instruction for a fresh approval.");
            }

            var financials = await Loaded.Financials(claims, claim.Value, instruction.Currency, cancellationToken);
            result = instruction.ReleaseInstruction(user.Value, claim.Value.Domain, financials.Outstanding(instruction.ReserveType), clock.UtcNow);
        }

        return result.IsFailure ? result.Error : InstructionView.From(instruction);
    }
}

internal sealed class RetryInstructionHandler(IClaimsRepository claims, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution, IClock clock,
    IAuditTrail audit)
    : ICommandHandler<RetryInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(RetryInstruction command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await claims.Instruction(command.InstructionId, cancellationToken) is not { } instruction)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment instruction was not found.");
        }

        var claim = await Loaded.Claim(claims, instruction.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        audit.Record(new AuditEntry("CLAIMS", "RetryInstruction", nameof(ClaimPaymentInstruction), instruction.Id.ToString()));
        var payee = await Payees.Account(bankAccounts, tenant, command.PayeePartyId, command.BankAccountId, instruction.Currency, cancellationToken);
        if (payee.IsFailure)
        {
            return payee.Error;
        }

        var result = instruction.Retry(payee.Value, claim.Value.Domain, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : InstructionView.From(instruction);
    }
}

internal sealed class RedirectInstructionHandler(IClaimsRepository claims, IBankAccounts bankAccounts, ITenantContext tenant, IExecutionContext execution, IClock clock,
    IAuditTrail audit) : ICommandHandler<RedirectInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(RedirectInstruction command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await claims.Instruction(command.InstructionId, cancellationToken) is not { } instruction)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The payment instruction was not found.");
        }

        var payee = await Payees.Account(bankAccounts, tenant, command.PayeePartyId, command.BankAccountId, instruction.Currency, cancellationToken);
        if (payee.IsFailure)
        {
            return payee.Error;
        }

        audit.Record(new AuditEntry("CLAIMS", "RedirectInstruction", nameof(ClaimPaymentInstruction), instruction.Id.ToString()));
        var result = instruction.Redirect(payee.Value, user.Value, clock.UtcNow);
        return result.IsFailure ? result.Error : InstructionView.From(instruction);
    }
}

internal sealed class GetClaimHandler(IClaimsRepository claims) : IQueryHandler<GetClaim, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(GetClaim query, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, query.ClaimNumber, cancellationToken);
        return claim.IsFailure ? claim.Error : ClaimView.From(claim.Value);
    }
}

internal sealed class GetFinancialsHandler(IClaimsRepository claims) : IQueryHandler<GetFinancials, FinancialsView>
{
    public async Task<Result<FinancialsView>> Handle(GetFinancials query, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, query.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        var movements = await claims.Movements(query.ClaimNumber, cancellationToken);
        return FinancialsView.From(new ClaimFinancials(query.ClaimNumber, claim.Value.Domain, movements.FirstOrDefault()?.Currency ?? "", movements));
    }
}

internal sealed class ListInstructionsHandler(IClaimsRepository claims) : IQueryHandler<ListInstructions, IReadOnlyList<InstructionView>>
{
    public async Task<Result<IReadOnlyList<InstructionView>>> Handle(ListInstructions query, CancellationToken cancellationToken) =>
        await claims.ByNumber(query.ClaimNumber, cancellationToken) is null
            ? Error.NotFound(ErrorCodes.NotFound, "The claim was not found.")
            : Result.Success<IReadOnlyList<InstructionView>>((await claims.Instructions(query.ClaimNumber, cancellationToken)).Select(InstructionView.From).ToList());
}

internal sealed class GetInstructionHandler(IClaimsRepository claims) : IQueryHandler<GetInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(GetInstruction query, CancellationToken cancellationToken) =>
        await claims.Instruction(query.InstructionId, cancellationToken) is { } i ? InstructionView.From(i) : Error.NotFound(ErrorCodes.NotFound, "The payment instruction was not found.");
}

/// <summary>
/// Claims' policies (domain-model.json: claims consumes): Workflow's decision on an instruction; the payout's outcome
/// (RecordOutcome); and its own ClaimPaymentCompleted, which records the payment movement against the reserve (CLM-004).
/// </summary>
public sealed class ClaimsPolicies(IClaimsRepository claims, IBankAccounts bankAccounts, ITenantContext tenant, IBusinessDate businessDate, IClock clock, ILogger<ClaimsPolicies> logger) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentInitiated>,
    IIntegrationEventHandler<PaymentFailed>,
    IIntegrationEventHandler<Contracts.ClaimPaymentCompleted>,
    IIntegrationEventHandler<GeneralClaimApproved>,
    IIntegrationEventHandler<LifeClaimApproved>,
    IIntegrationEventHandler<RecoveryRecorded>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, granted.SubjectHash, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, rejected.SubjectHash, cancellationToken);

    /// <summary>GAP-036: the instruction shows its payment's ID from when the payment is created.</summary>
    public async Task Handle(PaymentInitiated initiated, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (initiated.BusinessRefType == ClaimsApprovals.InstructionBusinessRefType && initiated.BusinessRefId is { } id
            && await claims.Instruction(id, cancellationToken) is { } instruction)
        {
            instruction.LinkPayment(initiated.PaymentId);
        }
    }

    public Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Outcome(succeeded.BusinessRefType, succeeded.BusinessRefId, true, succeeded.PaymentId, succeeded.ValueDate, cancellationToken);

    public Task Handle(PaymentFailed failed, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Outcome(failed.BusinessRefType, failed.BusinessRefId, false, failed.PaymentId, businessDate.Today, cancellationToken);

    public async Task Handle(Contracts.ClaimPaymentCompleted completed, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await claims.ByNumber(completed.ClaimNumber, cancellationToken) is not { } claim)
        {
            return;
        }

        var financials = new ClaimFinancials(claim.ClaimNumber, claim.Domain, completed.Currency, await claims.Movements(claim.ClaimNumber, cancellationToken));
        var movement = financials.RecordPayment(completed.MovementId, completed.ReserveType, completed.Amount, clock.UtcNow);

        // TODO(spec): a payment above the reserve left when it completes (reserve lowered after release) is not recorded; it needs an exception queue (OQ-058).
        if (movement.IsSuccess)
        {
            claims.Add(movement.Value);
        }
    }

    /// <summary>
    /// GeneralClaimApproved → DraftInstruction, then ReleaseInstruction (process "General claim to settlement"). The
    /// instruction is for the approved amount, never more. Released when the reserve covers it; otherwise it waits,
    /// approved, for the reserve to be raised and a release through the API.
    /// </summary>
    public async Task Handle(GeneralClaimApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var reference = $"SETTLEMENT:{approved.ApprovalId:N}";
        if (await claims.ByNumber(approved.ClaimNumber, cancellationToken) is not { } claim || claim.Domain != "GENERAL"
            || await claims.Instruction(claim.ClaimNumber, reference, cancellationToken) is not null)
        {
            return;
        }

        var instruction = ClaimPaymentInstruction.FromApprovedSettlement(Guid.CreateVersion7(), claim, reference,
            new PayeeAccount(approved.PayeePartyId, approved.BankAccountId, approved.PayeeBankHash), approved.Amount, approved.Currency, approved.ReserveType,
            approved.ApprovalId);
        if (instruction.IsFailure)
        {
            throw new InvalidOperationException($"Settlement of {approved.ClaimNumber} could not be instructed: {instruction.Error.Message}");
        }

        claims.Add(instruction.Value);
        if (!await Payees.Unchanged(bankAccounts, tenant, instruction.Value.Payee, cancellationToken))
        {
            Hold(instruction.Value, ReleaseHoldReasons.PayeeAccountChanged, "The payee's bank account changed after approval.", claim.Domain);
            return;
        }

        var financials = new ClaimFinancials(claim.ClaimNumber, claim.Domain, approved.Currency, await claims.Movements(claim.ClaimNumber, cancellationToken));
        Release(instruction.Value, approved.CheckerId, claim.Domain, financials.Outstanding(approved.ReserveType));
    }

    /// <summary>An automatic release: refused, the instruction is held with why, and the claims role gets a task (never a silent stop).</summary>
    private void Release(ClaimPaymentInstruction instruction, Guid releasedBy, string domain, decimal outstanding)
    {
        var released = instruction.ReleaseInstruction(releasedBy, domain, outstanding, clock.UtcNow);
        if (released.IsFailure)
        {
            Hold(instruction, released.Error.Code == ClaimsErrorCodes.ReserveBelowZero ? ReleaseHoldReasons.ReserveBelowZero : released.Error.Code,
                released.Error.Code == ClaimsErrorCodes.ReserveBelowZero
                    ? $"{instruction.Amount} {instruction.Currency} exceeds the outstanding {instruction.ReserveType} reserve of {outstanding}; raise the reserve, then release."
                    : released.Error.Message, domain);
        }
    }

    private void Hold(ClaimPaymentInstruction instruction, string reason, string detail, string domain)
    {
        instruction.HoldRelease(reason, detail, domain, clock.UtcNow);

        // The log alarm (Cloud Eye) matches ClaimPaymentReleaseHeld: claim number and reason only, never payee or bank details.
        logger.LogWarning("ClaimPaymentReleaseHeld: claim {ClaimNumber} instruction {InstructionId} held: {Reason}", instruction.ClaimNumber, instruction.Id, reason);
    }

    /// <summary>
    /// LifeClaimApproved → one instruction per payee, each for the payee's approved amount into the approved account, and
    /// released at once while the payee's account is unchanged (ADR-009, ADR-013). The claim approval covered each payee
    /// and account, so it stands in for the instructions' approval.
    /// </summary>
    public async Task Handle(LifeClaimApproved approved, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await claims.ByNumber(approved.ClaimNumber, cancellationToken) is not { Domain: "LIFE" } claim)
        {
            return;
        }

        var financials = new ClaimFinancials(claim.ClaimNumber, claim.Domain, approved.Currency, await claims.Movements(claim.ClaimNumber, cancellationToken));
        foreach (var (payee, index) in approved.Payees.Select((p, i) => (p, i + 1)))
        {
            var reference = $"LIFE:{approved.ApprovalId:N}:{index}";
            if (await claims.Instruction(claim.ClaimNumber, reference, cancellationToken) is not null)
            {
                continue;
            }

            var instruction = ClaimPaymentInstruction.FromApprovedSettlement(Guid.CreateVersion7(), claim, reference,
                new PayeeAccount(payee.PartyId, payee.BankAccountId, payee.BankAccountHash), payee.Amount, approved.Currency, approved.ReserveType, approved.ApprovalId);
            if (instruction.IsFailure)
            {
                throw new InvalidOperationException($"Claim {approved.ClaimNumber} could not be instructed: {instruction.Error.Message}");
            }

            claims.Add(instruction.Value);
            if (await Payees.Unchanged(bankAccounts, tenant, instruction.Value.Payee, cancellationToken))
            {
                Release(instruction.Value, approved.CheckerId, claim.Domain, financials.Outstanding(approved.ReserveType));
            }
            else
            {
                Hold(instruction.Value, ReleaseHoldReasons.PayeeAccountChanged, $"Payee {index}'s bank account changed after approval.", claim.Domain);
            }
        }
    }

    /// <summary>A domain's recovery is recorded as a claim financial movement (CLM-004).</summary>
    public async Task Handle(RecoveryRecorded recovered, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await claims.ByNumber(recovered.ClaimNumber, cancellationToken) is not { } claim)
        {
            return;
        }

        var financials = new ClaimFinancials(claim.ClaimNumber, claim.Domain, recovered.Currency, await claims.Movements(claim.ClaimNumber, cancellationToken));
        var movement = financials.RecordRecovery(recovered.ReserveType, recovered.Amount, businessDate.Today, clock.UtcNow);
        if (movement.IsSuccess)
        {
            claims.Add(movement.Value);
        }
    }

    /// <summary>A decision for other payee details or another amount than those now on the instruction does not apply.</summary>
    private async Task Decide(string businessRefType, Guid instructionId, bool approved, Guid approvalId, string? subjectHash, CancellationToken cancellationToken)
    {
        if (businessRefType == ClaimsApprovals.InstructionBusinessRefType && await claims.Instruction(instructionId, cancellationToken) is { } instruction
            && await claims.ByNumber(instruction.ClaimNumber, cancellationToken) is { } claim)
        {
            var decided = instruction.Decide(approved, approvalId, subjectHash, claim.Domain, clock.UtcNow);
            if (decided.IsFailure)
            {
                logger.LogWarning("Approval {ApprovalId} not applied to instruction {InstructionId}: {Code}", approvalId, instructionId, decided.Error.Code);
            }
        }
    }

    private async Task Outcome(string? businessRefType, Guid? instructionId, bool succeeded, Guid paymentId, DateOnly paidOn, CancellationToken cancellationToken)
    {
        if (businessRefType == ClaimsApprovals.InstructionBusinessRefType && instructionId is { } id && await claims.Instruction(id, cancellationToken) is { } instruction
            && await claims.ByNumber(instruction.ClaimNumber, cancellationToken) is { } claim)
        {
            instruction.RecordOutcome(succeeded, paymentId, claim.Domain, paidOn, clock.UtcNow);
        }
    }
}

/// <summary>The Claims Framework's registry for domain claims (<see cref="IClaimRegistry"/>), through its own commands and transactions.</summary>
public sealed class ClaimRegistry(IDispatcher dispatcher, ILogger<ClaimRegistry> logger) : IClaimRegistry
{
    public async Task<ClaimRegistrationResult> Register(string domain, Guid domainClaimId, Guid policyRef, ClaimOwner owner, JsonElement fraudFacts,
        InitialReserve? initialReserve, CancellationToken cancellationToken)
    {
        var allocated = await dispatcher.Send(new AllocateClaimNumber(domain, domainClaimId, policyRef, owner.ProductCode, owner.OrgNodeCode), cancellationToken);
        if (allocated.IsFailure)
        {
            return new ClaimRegistrationResult(false, null, allocated.Error.Code, allocated.Error.Message);
        }

        // Indicators that cannot be evaluated on these facts are skipped and logged; the claim is registered and screened by the rest.
        var screened = await dispatcher.Send(new EvaluateFraudIndicators(allocated.Value.ClaimNumber, fraudFacts), cancellationToken);
        if (screened.IsFailure)
        {
            // The indicators in force could not be read (an invalid definition, say). The claim is registered; screen it again
            // through POST /claims/claims/{number}/fraud-evaluations once the definitions are fixed.
            logger.LogError("Claim {ClaimNumber} was not screened for fraud: {ErrorCode}", allocated.Value.ClaimNumber, screened.Error.Code);
        }
        // OQ-079: the claim's initial reserve, set once: a repeat registration finds a reserve of that type and leaves it.
        if (initialReserve is not null
            && await dispatcher.Query(new GetFinancials(allocated.Value.ClaimNumber), cancellationToken) is { IsSuccess: true } financials
            && financials.Value.Movements.All(m => m.ReserveType != initialReserve.ReserveType))
        {
            var reserved = await dispatcher.Send(new MoveReserve(allocated.Value.ClaimNumber, ReserveAction.Set, initialReserve.ReserveType, initialReserve.Amount,
                initialReserve.Currency, initialReserve.Basis), cancellationToken);
            if (reserved.IsFailure)
            {
                // The claim is registered; without the reserve, a payment is held until one is set (ClaimPaymentReleaseHeld).
                logger.LogError("Claim {ClaimNumber}'s initial reserve was not set: {ErrorCode}", allocated.Value.ClaimNumber, reserved.Error.Code);
            }
        }

        return new ClaimRegistrationResult(true, allocated.Value.ClaimNumber, null, null);
    }
}

