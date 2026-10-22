using Insys.BuildingBlocks.Contracts;

namespace Insys.ClaimsFramework.Contracts;

public static class ClaimsPermissions
{
    public const string ClaimRead = "claim_register.read";
    public const string ClaimCreate = "claim_register.create";
    public const string ClaimUpdate = "claim_register.update";
    public const string FinancialsRead = "claim_financials.read";
    public const string FinancialsUpdate = "claim_financials.update";
    public const string InstructionRead = "claim_payment.read";
    public const string InstructionCreate = "claim_payment.create";
    public const string InstructionRelease = "claim_payment.release";
}

public static class ClaimsApprovals
{
    public const string InstructionBusinessRefType = "CLAIM_PAYMENT";
    public const string InstructionApprovalType = "CLAIM_PAYMENT";
}

[IntegrationEvent("claims.ClaimNumberAllocated")]
public sealed record ClaimNumberAllocated(string ClaimNumber, string Domain, Guid DomainClaimId, Guid PolicyRef) : IIntegrationEvent;

[IntegrationEvent("claims.FraudIndicatorRaised")]
public sealed record FraudIndicatorRaised(string ClaimNumber, string IndicatorCode, int Score, bool Referred) : IIntegrationEvent;

/// <summary>A reserve was set or changed (CLM-004). <paramref name="Change"/> is signed: positive raises the reserve.</summary>
[IntegrationEvent("claims.ClaimReserveChanged")]
public sealed record ClaimReserveChanged(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Change, decimal Outstanding, string Currency,
    DateOnly MovementDate) : IIntegrationEvent;

[IntegrationEvent("claims.ClaimRecoveryRecorded")]
public sealed record ClaimRecoveryRecorded(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency,
    DateOnly MovementDate) : IIntegrationEvent;

/// <summary>An approved instruction was released: Payments adds it to a payout batch. A retry is a new attempt, and a new payment (PAY-001).</summary>
[IntegrationEvent("claims.ClaimPaymentInstructed")]
public sealed record ClaimPaymentInstructed(Guid InstructionId, string ClaimNumber, string Domain, Guid PayeePartyId, decimal Amount, string Currency, int Attempt)
    : IIntegrationEvent;

[IntegrationEvent("claims.ClaimPaymentCompleted")]
public sealed record ClaimPaymentCompleted(Guid InstructionId, Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency,
    DateOnly PaidOn) : IIntegrationEvent;
