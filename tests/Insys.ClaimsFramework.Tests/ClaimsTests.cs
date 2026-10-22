using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.ClaimsFramework.Domain.Claims;
using Insys.Testing;

namespace Insys.ClaimsFramework.Tests;

public sealed class ClaimsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 21);

    private static ClaimRegistration Claim() =>
        ClaimRegistration.AllocateClaimNumber(Guid.CreateVersion7(), "GEN", "GENERAL", Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    private static ClaimFinancials Financials(ClaimRegistration claim) => new(claim.ClaimNumber, claim.Domain, "ZAR", []);

    private static ClaimPaymentInstruction Draft(ClaimRegistration claim, Guid maker, decimal amount = 400m) =>
        ClaimPaymentInstruction.DraftInstruction(Guid.CreateVersion7(), claim, "PAY-1", Account(Guid.CreateVersion7()), amount, 500m, "ZAR", "INDEMNITY", maker).Value;

    private static PayeeAccount Account(Guid party, string hash = "a1") => new(party, Guid.CreateVersion7(), hash.PadRight(64, '0'));

    [Fact]
    public void Instruction_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("claims", "ClaimPaymentInstruction")
            .Select(t => t.Replace("Approval granted", "ApprovalGranted", StringComparison.Ordinal).Replace("Approval rejected", "ApprovalRejected", StringComparison.Ordinal)
                .Replace("Payment succeeded", "PaymentSucceeded", StringComparison.Ordinal).Replace("Payment failed", "PaymentFailed", StringComparison.Ordinal)
                .Replace("Retried with new details", "RetriedWithNewDetails", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        ClaimPaymentInstruction.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void ClaimNumber_CarriesTheSchemePrefix_CLM005()
    {
        var claim = Claim();
        claim.ClaimNumber.ShouldStartWith("GEN-");
        claim.ClaimNumber.Length.ShouldBeLessThanOrEqualTo(25);
        claim.DomainEvents.OfType<ClaimNumberAllocated>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Reserve_NeverGoesBelowZero_CLM004()
    {
        var financials = Financials(Claim());
        financials.SetReserve("INDEMNITY", 1000m, Today, Now).IsSuccess.ShouldBeTrue();
        financials.ChangeReserve("INDEMNITY", -250m, Today, Now).IsSuccess.ShouldBeTrue();
        financials.Outstanding("INDEMNITY").ShouldBe(750m);

        financials.ChangeReserve("INDEMNITY", -750.01m, Today, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.ReserveBelowZero);
        financials.RecordPayment(Guid.CreateVersion7(), "INDEMNITY", 800m, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.ReserveBelowZero);

        financials.SetReserve("INDEMNITY", 300m, Today, Now).Value.Amount.ShouldBe(-450m);
        financials.Movements.Count.ShouldBe(3);
    }

    [Fact]
    public void Payment_DrawsDownTheReserve_AndRecoveriesDoNotRaiseIt()
    {
        var financials = Financials(Claim());
        financials.SetReserve("INDEMNITY", 1000m, Today, Now);
        financials.RecordPayment(Guid.CreateVersion7(), "INDEMNITY", 400m, Now).IsSuccess.ShouldBeTrue();
        financials.RecordRecovery("INDEMNITY", 150m, Today, Now).IsSuccess.ShouldBeTrue();

        financials.Outstanding("INDEMNITY").ShouldBe(600m);
        financials.Movements.Select(m => m.MovementType).ShouldBe([MovementType.Reserve, MovementType.Payment, MovementType.Recovery]);
    }

    [Fact]
    public void Instruction_AboveTheApprovedAmount_IsRefused() =>
        ClaimPaymentInstruction.DraftInstruction(Guid.CreateVersion7(), Claim(), "PAY-1", Account(Guid.CreateVersion7()), 500.01m, 500m, "ZAR", "INDEMNITY", Guid.CreateVersion7())
            .Error!.Code.ShouldBe(ClaimsErrorCodes.AboveApprovedAmount);

    [Fact]
    public void Instruction_IsReleasedOnlyAfterApproval_SRS002()
    {
        var claim = Claim();
        var maker = Guid.CreateVersion7();
        var instruction = Draft(claim, maker);

        instruction.ReleaseInstruction(maker, claim.Domain, 1000m, Now).IsFailure.ShouldBeTrue();
        instruction.SubmitInstruction(maker, Now).IsSuccess.ShouldBeTrue();
        instruction.DomainEvents.OfType<InstructionSubmitted>().Single().Amount.ShouldBe(400m);
        instruction.ReleaseInstruction(maker, claim.Domain, 1000m, Now).IsFailure.ShouldBeTrue();
        instruction.DomainEvents.OfType<ClaimPaymentInstructed>().ShouldBeEmpty();

        instruction.Decide(approved: true, Guid.CreateVersion7(), "another subject", claim.Domain, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.ApprovalSubjectChanged);
        instruction.Status.ShouldBe(InstructionStatus.PendingApproval);
        instruction.Decide(approved: true, Guid.CreateVersion7(), instruction.SubjectHash, claim.Domain, Now).IsSuccess.ShouldBeTrue();
        instruction.ReleaseInstruction(maker, claim.Domain, 399.99m, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.ReserveBelowZero);
        instruction.ReleaseInstruction(maker, claim.Domain, 400m, Now).IsSuccess.ShouldBeTrue();
        instruction.DomainEvents.OfType<ClaimPaymentInstructed>().Single().Attempt.ShouldBe(1);
    }

    [Fact]
    public void RejectedInstruction_IsNeverReleased()
    {
        var claim = Claim();
        var instruction = Draft(claim, Guid.CreateVersion7());
        instruction.SubmitInstruction(Guid.CreateVersion7(), Now);
        instruction.Decide(approved: false, Guid.CreateVersion7(), instruction.SubjectHash, claim.Domain, Now);

        instruction.Status.ShouldBe(InstructionStatus.Rejected);
        instruction.ReleaseInstruction(Guid.CreateVersion7(), claim.Domain, 1000m, Now).IsFailure.ShouldBeTrue();
    }

    private static ClaimPaymentInstruction FailedPayment(ClaimRegistration claim, PayeeAccount payee)
    {
        var instruction = ClaimPaymentInstruction.DraftInstruction(Guid.CreateVersion7(), claim, "PAY-1", payee, 400m, 500m, "ZAR", "INDEMNITY", Guid.CreateVersion7()).Value;
        instruction.SubmitInstruction(Guid.CreateVersion7(), Now);
        instruction.Decide(true, Guid.CreateVersion7(), instruction.SubjectHash, claim.Domain, Now);
        instruction.ReleaseInstruction(Guid.CreateVersion7(), claim.Domain, 400m, Now);
        instruction.RecordOutcome(false, Guid.CreateVersion7(), claim.Domain, Today, Now);
        instruction.Status.ShouldBe(InstructionStatus.Failed);
        return instruction;
    }

    [Fact]
    public void FailedPayment_RetriedToTheApprovedAccount_IsReleasedAtOnce_AsANewAttempt()
    {
        var claim = Claim();
        var payee = Account(Guid.CreateVersion7());
        var instruction = FailedPayment(claim, payee);

        instruction.Retry(payee, claim.Domain, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        instruction.Status.ShouldBe(InstructionStatus.Released);
        instruction.DomainEvents.OfType<InstructionRetryRequested>().ShouldBeEmpty();
        instruction.DomainEvents.OfType<ClaimPaymentInstructed>().Last().ShouldSatisfyAllConditions(e => e.Attempt.ShouldBe(2), e => e.BankAccountId.ShouldBe(payee.BankAccountId));
        instruction.RecordOutcome(true, Guid.CreateVersion7(), claim.Domain, Today, Now);
        instruction.Status.ShouldBe(InstructionStatus.Paid);
        instruction.DomainEvents.OfType<ClaimPaymentCompleted>().Single().Amount.ShouldBe(400m);
    }

    [Fact]
    public void NewPayeeOrBankAccount_AfterApproval_NeedsANewApproval_SRS002()
    {
        var claim = Claim();
        var payee = Account(Guid.CreateVersion7());
        var instruction = FailedPayment(claim, payee);
        var clerk = Guid.CreateVersion7();
        var newAccount = payee with { BankAccountId = Guid.CreateVersion7(), AccountHash = "b2".PadRight(64, '0') };

        instruction.Retry(newAccount, claim.Domain, clerk, Now).IsSuccess.ShouldBeTrue();

        instruction.Status.ShouldBe(InstructionStatus.Failed);
        instruction.PendingBankAccountId.ShouldBe(newAccount.BankAccountId);
        var requested = instruction.DomainEvents.OfType<InstructionRetryRequested>().Single();
        requested.RequestedBy.ShouldBe(clerk);
        requested.SubjectHash.ShouldBe(instruction.PendingSubjectHash);
        instruction.DomainEvents.OfType<ClaimPaymentInstructed>().Count().ShouldBe(1);
        instruction.Retry(Account(Guid.CreateVersion7()), claim.Domain, clerk, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.RetryPending);

        // The earlier approval (for the old account) does not cover the new one.
        instruction.Decide(true, Guid.CreateVersion7(), instruction.SubjectHash, claim.Domain, Now).Error!.Code.ShouldBe(ClaimsErrorCodes.ApprovalSubjectChanged);
        instruction.Status.ShouldBe(InstructionStatus.Failed);

        instruction.Decide(true, Guid.CreateVersion7(), instruction.PendingSubjectHash, claim.Domain, Now).IsSuccess.ShouldBeTrue();
        instruction.Status.ShouldBe(InstructionStatus.Released);
        instruction.Payee.ShouldBe(newAccount);
        instruction.DomainEvents.OfType<ClaimPaymentInstructed>().Last().ShouldSatisfyAllConditions(e => e.Attempt.ShouldBe(2),
            e => e.BankAccountId.ShouldBe(newAccount.BankAccountId));
    }

    [Fact]
    public void ApprovedInstruction_RedirectedToANewAccount_StaysApprovedUntilTheNewApproval()
    {
        var claim = Claim();
        var instruction = Draft(claim, Guid.CreateVersion7());
        instruction.SubmitInstruction(Guid.CreateVersion7(), Now);
        instruction.Decide(true, Guid.CreateVersion7(), instruction.SubjectHash, claim.Domain, Now);
        var newAccount = instruction.Payee! with { BankAccountId = Guid.CreateVersion7() };

        instruction.Redirect(newAccount, Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        instruction.Status.ShouldBe(InstructionStatus.Approved);
        instruction.Decide(true, Guid.CreateVersion7(), instruction.PendingSubjectHash, claim.Domain, Now).IsSuccess.ShouldBeTrue();

        instruction.Status.ShouldBe(InstructionStatus.Approved);
        instruction.BankAccountId.ShouldBe(newAccount.BankAccountId);
        instruction.ReleaseInstruction(Guid.CreateVersion7(), claim.Domain, 1000m, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void RejectedNewAccount_LeavesThePaymentFailed()
    {
        var claim = Claim();
        var payee = Account(Guid.CreateVersion7());
        var instruction = FailedPayment(claim, payee);
        instruction.Retry(Account(Guid.CreateVersion7()), claim.Domain, Guid.CreateVersion7(), Now);

        instruction.Decide(false, Guid.CreateVersion7(), instruction.PendingSubjectHash, claim.Domain, Now).IsSuccess.ShouldBeTrue();

        instruction.Status.ShouldBe(InstructionStatus.Failed);
        instruction.Payee.ShouldBe(payee);
        instruction.PendingPayeePartyId.ShouldBeNull();
    }

    [Fact]
    public void FraudIndicators_ReferTheClaim_WhenTheTotalReachesTheThreshold()
    {
        var claim = Claim();
        List<FraudIndicator> indicators =
        [
            new("EARLY_CLAIM", Expression.Parse("days_since_inception < 30").Value, 40, 70),
            new("HIGH_VALUE", Expression.Parse("amount > 100000").Value, 35, 70),
            new("REPEAT", Expression.Parse("prior_claims > 3").Value, 50, 70),
        ];

        claim.EvaluateFraudIndicators(indicators, new Dictionary<string, object?> { ["days_since_inception"] = 10m, ["amount"] = 250000m, ["prior_claims"] = 0m }, Now)
            .Value.ShouldBe(75);

        claim.FraudHits.Select(h => h.IndicatorCode).ShouldBe(["EARLY_CLAIM", "HIGH_VALUE"]);
        claim.FraudHits.ShouldAllBe(h => h.Referred);
    }

    [Fact]
    public void FraudIndicator_ThatCannotBeEvaluated_FailsTheEvaluation()
    {
        var claim = Claim();
        claim.EvaluateFraudIndicators([new("X", Expression.Parse("missing > 1").Value, 10, 10)], new Dictionary<string, object?>(), Now).Error!.Code
            .ShouldBe(ExpressionErrors.EvaluationCode);
        claim.FraudHits.ShouldBeEmpty();
    }

    [Fact]
    public void Evidence_FulfilsAnOpenRequest()
    {
        var claim = Claim();
        claim.RequestEvidence("POLICE_REPORT", Now).IsSuccess.ShouldBeTrue();
        claim.RequestEvidence("POLICE_REPORT", Now).Error!.Code.ShouldBe(ClaimsErrorCodes.EvidenceRequested);

        claim.RecordEvidence("POLICE_REPORT", Guid.CreateVersion7(), Now);
        claim.RecordEvidence("PHOTOS", Guid.CreateVersion7(), Now);

        claim.Evidence.Count.ShouldBe(2);
        claim.Evidence.ShouldAllBe(e => e.Status == "RECEIVED");
    }
}
