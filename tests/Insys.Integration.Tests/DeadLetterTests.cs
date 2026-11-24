/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration.Tests
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
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Domain.DeadLetters;
using Insys.Testing;

namespace Insys.Integration.Tests;

public sealed class DeadLetterTests
{
    private static readonly Guid Operator = Guid.CreateVersion7();
    private static readonly Guid Checker = Guid.CreateVersion7();
    private const string Reason = "The payment was made by hand; this event no longer applies.";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static DeadLetter NewDeadLetter() => DeadLetter.DeadLetterMessage(Guid.CreateVersion7(), new Source("outbox:party"),
        new MessageRef(Guid.NewGuid().ToString()), "{}", new DeliveryError("System.TimeoutException"), DateTimeOffset.UtcNow, 10);

    [Fact]
    public void DeadLetter_DeadLetterMessage_StartsDeadLettered()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
        deadLetter.ResolvedBy.ShouldBeNull();
        deadLetter.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void DeadLetter_ReplayMessage_FromDeadLettered_MovesToReplayed_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.ReplayMessage(Operator).IsSuccess.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.Replayed);
        deadLetter.ResolvedBy.ShouldBe(Operator);
        deadLetter.HasChanges.ShouldBeTrue();
    }

    [Fact]
    public void DeadLetter_DiscardMessage_FromDeadLettered_MovesToDiscarded_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.DiscardMessage(Operator, Reason).IsSuccess.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.Discarded);
        deadLetter.DiscardReason.ShouldBe(Reason);
    }

    [Theory]
    [InlineData(DeadLetterTrigger.ReplayMessage, DeadLetterTrigger.ReplayMessage)]
    [InlineData(DeadLetterTrigger.ReplayMessage, DeadLetterTrigger.DiscardMessage)]
    [InlineData(DeadLetterTrigger.DiscardMessage, DeadLetterTrigger.ReplayMessage)]
    [InlineData(DeadLetterTrigger.DiscardMessage, DeadLetterTrigger.DiscardMessage)]
    public void DeadLetter_ResolvedMessage_CannotBeResolvedAgain_IsInvalidStateTransition(DeadLetterTrigger first, DeadLetterTrigger second)
    {
        var deadLetter = NewDeadLetter();
        Fire(deadLetter, first).IsSuccess.ShouldBeTrue();

        var result = Fire(deadLetter, second);

        result.Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void DeadLetter_ReplayedOnlyByAnAuthorisedOperator_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.ReplayMessage(Guid.Empty).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
    }

    [Fact]
    public void DeadLetter_Lifecycle_MatchesDomainModelStateTransitions()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Specs, "domain-model.json")));
        var specified = model.RootElement.GetProperty("contexts").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "integration")
            .GetProperty("aggregates").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "DeadLetter")
            .GetProperty("stateTransitions").EnumerateArray()
            .Select(t => (Normalise(t.GetProperty("from").GetString()!), t.GetProperty("trigger").GetString()!, Normalise(t.GetProperty("to").GetString()!)))
            .OrderBy(t => t.ToString())
            .ToList();

        var implemented = DeadLetter.Lifecycle.Transitions.Select(t => (t.From.ToString(), t.Trigger.ToString(), t.To.ToString())).OrderBy(t => t.ToString()).ToList();

        implemented.ShouldBe(specified);
    }

    [Fact]
    public void DeadLetter_Discard_NeedsAReasonOfAtMost300Characters_DE007()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.DiscardMessage(Operator, " ").Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        deadLetter.DiscardMessage(Operator, new string('x', 301)).Error!.Code.ShouldBe(ErrorCodes.ValidationFailed);
        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
    }

    [Fact]
    public void DeadLetter_RequestDiscard_IsDiscardRequested_AndAsksForASecondPersonsApproval_DE007()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.RequestDiscard(Operator, Reason, Now).IsSuccess.ShouldBeTrue();

        deadLetter.Status.ShouldBe(DeadLetterStatus.DiscardRequested);
        deadLetter.Resolution.ShouldBeNull();
        deadLetter.DiscardRequestedBy.ShouldBe(Operator);
        deadLetter.DiscardRequestedAt.ShouldBe(Now);
        deadLetter.DiscardReason.ShouldBe(Reason);
        var requested = deadLetter.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DiscardRequested>();
        requested.RequestedBy.ShouldBe(Operator);
        requested.SubjectHash.ShouldBe(deadLetter.DiscardSubjectHash);
        requested.SubjectHash.ShouldBe(DeadLetter.DiscardSubject(deadLetter.Id, deadLetter.MessageRef.Value, Reason, Now));
    }

    [Fact]
    public void DeadLetter_WhileADiscardIsRequested_ReplayAndAnotherDiscardAreRefused_DE007()
    {
        var deadLetter = NewDeadLetter();
        deadLetter.RequestDiscard(Operator, Reason, Now).IsSuccess.ShouldBeTrue();

        foreach (var refused in new[] { deadLetter.ReplayMessage(Operator), deadLetter.RequestDiscard(Operator, Reason, Now), deadLetter.DiscardMessage(Operator, Reason) })
        {
            refused.Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
            refused.Error.Kind.ShouldBe(ErrorKind.Conflict);
            refused.Error.Message.ShouldContain("second person");
        }

        deadLetter.Status.ShouldBe(DeadLetterStatus.DiscardRequested);
    }

    [Fact]
    public void DeadLetter_ARequestedDiscard_GrantedByAnotherPerson_IsDiscardedAsTheRequesterAsked_SRS002_DE007()
    {
        var deadLetter = NewDeadLetter();
        deadLetter.RequestDiscard(Operator, Reason, Now);

        deadLetter.ApproveDiscard(Checker, deadLetter.DiscardSubjectHash).IsSuccess.ShouldBeTrue();

        deadLetter.Status.ShouldBe(DeadLetterStatus.Discarded);
        deadLetter.ResolvedBy.ShouldBe(Operator);
        deadLetter.DiscardApprovedBy.ShouldBe(Checker);
        deadLetter.DiscardReason.ShouldBe(Reason);
        deadLetter.DiscardGrantedBy(Checker).ShouldBeTrue();
    }

    [Fact]
    public void DeadLetter_ARequestedDiscard_IsNeverGrantedByTheRequester_OrForAnotherSubject_SRS002_DE007()
    {
        var deadLetter = NewDeadLetter();
        deadLetter.RequestDiscard(Operator, Reason, Now);

        deadLetter.ApproveDiscard(Operator, deadLetter.DiscardSubjectHash).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        deadLetter.ApproveDiscard(Checker, "another subject").Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        deadLetter.Status.ShouldBe(DeadLetterStatus.DiscardRequested);
    }

    [Fact]
    public void DeadLetter_AGrant_WithNoDiscardRequested_IsNotApplied_DE007()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.ApproveDiscard(Checker, null).IsFailure.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeadLetter_ARequestedDiscard_EndingWithoutApproval_ClearsTheRequest_AndStaysDeadLettered_OQ113_DE007(bool rejectedByAChecker)
    {
        var deadLetter = NewDeadLetter();
        deadLetter.RequestDiscard(Operator, Reason, Now);

        deadLetter.ClearDiscardRequest(rejectedByAChecker ? Checker : Guid.Empty, deadLetter.DiscardSubjectHash).IsSuccess.ShouldBeTrue();

        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
        deadLetter.DiscardRequestedBy.ShouldBeNull();
        deadLetter.DiscardRequestedAt.ShouldBeNull();
        deadLetter.DiscardReason.ShouldBeNull();
        deadLetter.DiscardSubjectHash.ShouldBeNull();
        deadLetter.ReplayMessage(Operator).IsSuccess.ShouldBeTrue("once the request is cleared, the dead letter can be replayed");
    }

    [Fact]
    public void DeadLetter_AnEndingForAnotherRequest_IsRefused_AndARedeliveredEndingChangesNothing_DE007()
    {
        var deadLetter = NewDeadLetter();
        deadLetter.RequestDiscard(Operator, Reason, Now);
        deadLetter.ClearDiscardRequest(Checker, "an earlier request").IsFailure.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.DiscardRequested);

        deadLetter.ClearDiscardRequest(Checker, deadLetter.DiscardSubjectHash).IsSuccess.ShouldBeTrue();
        deadLetter.ClearDiscardRequest(Checker, "redelivered").IsSuccess.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
    }

    [Fact]
    public void DeadLetter_AResolvedMessage_CannotBeAskedToBeDiscarded_DE007()
    {
        var deadLetter = NewDeadLetter();
        deadLetter.ReplayMessage(Operator);

        deadLetter.RequestDiscard(Operator, Reason, Now).Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        deadLetter.DiscardRequestedBy.ShouldBeNull();
    }

    [Theory]
    [InlineData("outbox:payment", "payment.PaymentFailed", true)]
    [InlineData("outbox:billing", "billing.InvoiceIssued", true)]
    [InlineData("outbox:finance", "finance.JournalPosted", true)]
    [InlineData("outbox:claims", "claims.ClaimPaymentApproved", true)]
    [InlineData("outbox:commission", "commission.CommissionStatementApproved", true)]
    [InlineData("outbox:billing", "workflow.ApprovalRequested", true)]
    [InlineData("outbox:health", "health.ProviderPaymentRunReleased", true)]
    [InlineData("outbox:health", "health.ProviderRemittanceIssued", true)]
    [InlineData("outbox:health", "health.ProviderRecoveryRaised", true)]
    [InlineData("outbox:health", "health.HealthCreditNoteRequested", true)]
    [InlineData("outbox:workflow", "workflow.CaseRedressApproved", true)]
    [InlineData("outbox:health", "health.HealthClaimSubmitted", false)]
    [InlineData("outbox:workflow", "workflow.ApprovalGranted", false)]
    [InlineData("outbox:life", "life.LifePolicyIssued", false)]
    [InlineData("outbox:general", "general.GeneralClaimPaymentRequested", false)]
    [InlineData("outbox:party", "party.PartyCreated", false)]
    [InlineData("outbox:reinsurance", "reinsurance.CessionCalculated", false)]
    [InlineData("ledger:document.LifeDocumentPolicies.LifePolicyIssued", "life.LifePolicyIssued", false)]
    [InlineData("ledger:billing.BillingPolicies.PaymentAllocated", "payment.PaymentAllocated", true)]
    [InlineData("ledger:finance.Postings.HealthProviderPaymentRunReleased", "health.ProviderPaymentRunReleased", true)]
    [InlineData("webhook:SUB-1", null, false)]
    public void DiscardApproval_MoneyMessages_NeedASecondPerson_DE007(string source, string? eventType, bool money) =>
        DiscardApproval.NeedsSecondPerson(source, eventType).ShouldBe(money);

    private static string Normalise(string state) => string.Concat(state.Split('-', ' ').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static Result Fire(DeadLetter deadLetter, DeadLetterTrigger trigger) =>
        trigger == DeadLetterTrigger.ReplayMessage ? deadLetter.ReplayMessage(Operator) : deadLetter.DiscardMessage(Operator, Reason);
}
