using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.ClaimsFramework.Contracts;
using Insys.ClaimsFramework.Domain.Claims;
using Insys.Payments.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.ClaimsFramework.Application;

public interface IClaimsRepository
{
    Task<ClaimRegistration?> ByNumber(string claimNumber, CancellationToken cancellationToken);

    Task<ClaimRegistration?> ByDomainClaim(string domain, Guid domainClaimId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialMovement>> Movements(string claimNumber, CancellationToken cancellationToken);

    Task<ClaimPaymentInstruction?> Instruction(Guid id, CancellationToken cancellationToken);

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

public sealed record EvidenceView(string EvidenceType, string Status, Guid? DocumentId, DateTimeOffset? RequestedAt, DateTimeOffset? ReceivedAt);

public sealed record FraudHitView(string IndicatorCode, int Score, bool Referred, DateTimeOffset EvaluatedAt);

public sealed record ClaimView(Guid Id, string ClaimNumber, string Domain, Guid DomainClaimId, Guid PolicyRef, DateTimeOffset RegisteredAt,
    IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<FraudHitView> FraudHits, int Communications)
{
    public static ClaimView From(ClaimRegistration c) => new(c.Id, c.ClaimNumber, c.Domain, c.DomainClaimId, c.PolicyRef, c.RegisteredAt,
        c.Evidence.Select(e => new EvidenceView(e.EvidenceType, e.Status, e.DocumentId, e.RequestedAt, e.ReceivedAt)).ToList(),
        c.FraudHits.Select(h => new FraudHitView(h.IndicatorCode, h.Score, h.Referred, h.EvaluatedAt)).ToList(), c.Communications.Count);
}

public sealed record MovementView(Guid Id, string MovementType, string ReserveType, decimal Amount, string Currency, DateTimeOffset MovementAt);

public sealed record FinancialsView(string ClaimNumber, IReadOnlyList<MovementView> Movements, IReadOnlyDictionary<string, decimal> OutstandingReserves)
{
    public static FinancialsView From(ClaimFinancials f) => new(f.ClaimNumber,
        f.Movements.OrderBy(m => m.MovementAt).ThenBy(m => m.Id).Select(m => new MovementView(m.Id, m.MovementType.ToString(), m.ReserveType, m.Amount, m.Currency, m.MovementAt)).ToList(),
        f.Movements.Select(m => m.ReserveType).Distinct().ToDictionary(t => t, f.Outstanding));
}

public sealed record InstructionView(Guid Id, string ClaimNumber, string InstructionRef, Guid PayeePartyId, decimal Amount, decimal ApprovedAmount, string Currency,
    string ReserveType, string Status, Guid? ApprovalId, Guid? PaymentId, int Attempt)
{
    public static InstructionView From(ClaimPaymentInstruction i) => new(i.Id, i.ClaimNumber, i.InstructionRef, i.PayeePartyId, i.Amount, i.ApprovedAmount, i.Currency,
        i.ReserveType, i.Status.ToString(), i.ApprovalId, i.PaymentId, i.Attempt);
}

/// <summary>Command AllocateClaimNumber; a repeat for the same domain claim returns the same number (CLM-005).</summary>
public sealed record AllocateClaimNumber(string Domain, Guid DomainClaimId, Guid PolicyRef) : ICommand<ClaimView>;

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
public sealed record MoveReserve(string ClaimNumber, ReserveAction Action, string ReserveType, decimal Amount, string Currency) : ICommand<FinancialsView>;

/// <summary>Command DraftInstruction; a repeat for the same claim and reference returns the same instruction (PAY-001).</summary>
public sealed record DraftInstruction(string ClaimNumber, string InstructionRef, Guid PayeePartyId, decimal Amount, decimal ApprovedAmount, string Currency,
    string ReserveType) : ICommand<InstructionView>;

public enum InstructionAction
{
    Submit,
    Release,
}

public sealed record ChangeInstruction(Guid InstructionId, InstructionAction Action) : ICommand<InstructionView>;

public sealed record RetryInstruction(Guid InstructionId, Guid PayeePartyId) : ICommand<InstructionView>;

public sealed record GetClaim(string ClaimNumber) : IQuery<ClaimView>;

public sealed record GetFinancials(string ClaimNumber) : IQuery<FinancialsView>;

public sealed record GetInstruction(Guid InstructionId) : IQuery<InstructionView>;

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

internal sealed class AllocateClaimNumberHandler(IClaimsRepository claims, IClaimsCodes codes, IClock clock) : ICommandHandler<AllocateClaimNumber, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(AllocateClaimNumber command, CancellationToken cancellationToken)
    {
        if (await claims.ByDomainClaim(command.Domain, command.DomainClaimId, cancellationToken) is { } existing)
        {
            return ClaimView.From(existing);
        }

        var prefix = await codes.NumberPrefix(command.Domain, clock.UtcToday, cancellationToken) ?? command.Domain[..3];
        var claim = ClaimRegistration.AllocateClaimNumber(Guid.CreateVersion7(), prefix, command.Domain, command.DomainClaimId, command.PolicyRef, clock.UtcNow);
        claims.Add(claim);
        return ClaimView.From(claim);
    }
}

internal sealed class RequestEvidenceHandler(IClaimsRepository claims, IClaimsCodes codes, IClock clock) : ICommandHandler<RequestEvidence, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RequestEvidence command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.EvidenceTypeExists(command.EvidenceType, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.EvidenceType} is not an evidence type in force.");
        }

        var result = claim.Value.RequestEvidence(command.EvidenceType, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim.Value);
    }
}

internal sealed class RecordEvidenceHandler(IClaimsRepository claims, IClaimsCodes codes, IClock clock) : ICommandHandler<RecordEvidence, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(RecordEvidence command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.EvidenceTypeExists(command.EvidenceType, clock.UtcToday, cancellationToken))
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

internal sealed class EvaluateFraudIndicatorsHandler(IClaimsRepository claims, IClaimsCodes codes, IClock clock) : ICommandHandler<EvaluateFraudIndicators, ClaimView>
{
    public async Task<Result<ClaimView>> Handle(EvaluateFraudIndicators command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        var indicators = await codes.FraudIndicators(clock.UtcToday, cancellationToken);
        if (indicators.IsFailure)
        {
            return indicators.Error;
        }

        var facts = command.Facts.ValueKind == JsonValueKind.Object
            ? command.Facts.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>();
        var result = claim.Value.EvaluateFraudIndicators(indicators.Value, facts, clock.UtcNow);
        return result.IsFailure ? result.Error : ClaimView.From(claim.Value);
    }
}

internal sealed class MoveReserveHandler(IClaimsRepository claims, IClaimsCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<MoveReserve, FinancialsView>
{
    public async Task<Result<FinancialsView>> Handle(MoveReserve command, CancellationToken cancellationToken)
    {
        var claim = await Loaded.Claim(claims, command.ClaimNumber, cancellationToken);
        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!await codes.ReserveTypeExists(command.ReserveType, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.ReserveType} is not a reserve type in force.");
        }

        var financials = await Loaded.Financials(claims, claim.Value, command.Currency, cancellationToken);
        audit.Record(new AuditEntry("CLAIMS", $"Reserve{command.Action}", nameof(ClaimFinancials), claim.Value.Id.ToString(),
            new Dictionary<string, string?> { ["claim"] = command.ClaimNumber, ["reserveType"] = command.ReserveType }));
        var movement = command.Action switch
        {
            ReserveAction.Set => financials.SetReserve(command.ReserveType, command.Amount, clock.UtcToday, clock.UtcNow),
            ReserveAction.Change => financials.ChangeReserve(command.ReserveType, command.Amount, clock.UtcToday, clock.UtcNow),
            _ => financials.RecordRecovery(command.ReserveType, command.Amount, clock.UtcToday, clock.UtcNow),
        };
        if (movement.IsFailure)
        {
            return movement.Error;
        }

        claims.Add(movement.Value);
        return FinancialsView.From(financials);
    }
}

internal sealed class DraftInstructionHandler(IClaimsRepository claims, IClaimsCodes codes, IExecutionContext execution, IClock clock, IAuditTrail audit)
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

        if (!await codes.ReserveTypeExists(command.ReserveType, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(ClaimsErrorCodes.CodeNotInForce, $"{command.ReserveType} is not a reserve type in force.");
        }

        var instruction = ClaimPaymentInstruction.DraftInstruction(Guid.CreateVersion7(), claim.Value, command.InstructionRef, command.PayeePartyId, command.Amount,
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

internal sealed class ChangeInstructionHandler(IClaimsRepository claims, IExecutionContext execution, IClock clock, IAuditTrail audit)
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

            var financials = await Loaded.Financials(claims, claim.Value, instruction.Currency, cancellationToken);
            result = instruction.ReleaseInstruction(user.Value, claim.Value.Domain, financials.Outstanding(instruction.ReserveType), clock.UtcNow);
        }

        return result.IsFailure ? result.Error : InstructionView.From(instruction);
    }
}

internal sealed class RetryInstructionHandler(IClaimsRepository claims, IExecutionContext execution, IClock clock, IAuditTrail audit)
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
        var result = instruction.Retry(command.PayeePartyId, claim.Value.Domain, user.Value, clock.UtcNow);
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

internal sealed class GetInstructionHandler(IClaimsRepository claims) : IQueryHandler<GetInstruction, InstructionView>
{
    public async Task<Result<InstructionView>> Handle(GetInstruction query, CancellationToken cancellationToken) =>
        await claims.Instruction(query.InstructionId, cancellationToken) is { } i ? InstructionView.From(i) : Error.NotFound(ErrorCodes.NotFound, "The payment instruction was not found.");
}

/// <summary>
/// Claims' policies (domain-model.json: claims consumes): Workflow's decision on an instruction; the payout's outcome
/// (RecordOutcome); and its own ClaimPaymentCompleted, which records the payment movement against the reserve (CLM-004).
/// </summary>
public sealed class ClaimsPolicies(IClaimsRepository claims, IClock clock) :
    IIntegrationEventHandler<ApprovalGranted>,
    IIntegrationEventHandler<ApprovalRejected>,
    IIntegrationEventHandler<PaymentSucceeded>,
    IIntegrationEventHandler<PaymentFailed>,
    IIntegrationEventHandler<Contracts.ClaimPaymentCompleted>
{
    public Task Handle(ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, approved: true, granted.ApprovalId, cancellationToken);

    public Task Handle(ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, approved: false, rejected.ApprovalId, cancellationToken);

    public Task Handle(PaymentSucceeded succeeded, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Outcome(succeeded.BusinessRefType, succeeded.BusinessRefId, true, succeeded.PaymentId, succeeded.ValueDate, cancellationToken);

    public Task Handle(PaymentFailed failed, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Outcome(failed.BusinessRefType, failed.BusinessRefId, false, failed.PaymentId, clock.UtcToday, cancellationToken);

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

    private async Task Decide(string businessRefType, Guid instructionId, bool approved, Guid approvalId, CancellationToken cancellationToken)
    {
        if (businessRefType == ClaimsApprovals.InstructionBusinessRefType && await claims.Instruction(instructionId, cancellationToken) is { } instruction)
        {
            instruction.Decide(approved, approvalId);
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
