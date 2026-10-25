/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance.Tests
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
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Finance.Domain.Ledger;
using Insys.Testing;

namespace Insys.Finance.Tests;

public sealed class LedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 21);
    private static readonly ExchangeRate Base = new("ZAR", 1m);
    private static readonly PostingRule Premium = new("PR-PREM", 3, "PREMIUM_BILLED", null, "1100", "4000");

    private static AccountingPeriod OpenOctober()
    {
        var period = AccountingPeriod.Define(Guid.CreateVersion7(), "2026-10", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        period.OpenPeriod().IsSuccess.ShouldBeTrue();
        return period;
    }

    private static BusinessEvent Event(decimal amount, string type = "PREMIUM_BILLED", Dictionary<string, object?>? variables = null) =>
        new(Guid.CreateVersion7(), "BILLING", type, Day, Day, amount, "ZAR", "INVOICE", Guid.CreateVersion7(), variables ?? []);

    [Fact]
    public void AccountingPeriod_Lifecycle_MatchesDomainModel()
    {
        var specified = DomainModelSpec.Transitions("finance", "AccountingPeriod").Select(t => t.Replace("Reopened with approval", "ReopenedWithApproval", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        AccountingPeriod.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ToList().ShouldBe(specified);
    }

    [Fact]
    public void PostBusinessEvent_DebitsEqualCredits_WithRuleVersionOnEveryLine_FIN004()
    {
        var journal = Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(1234.56m), Premium, OpenOctober(), Base, Now).Value;

        journal.Lines.Count.ShouldBe(2);
        journal.Lines.Sum(l => l.Debit).ShouldBe(journal.Lines.Sum(l => l.Credit));
        journal.Lines.Single(l => l.Debit > 0).AccountCode.ShouldBe("1100");
        journal.Lines.Single(l => l.Credit > 0).AccountCode.ShouldBe("4000");
        journal.Lines.ShouldAllBe(l => l.PostingRuleCode == "PR-PREM" && l.PostingRuleVersion == 3 && l.FxRate == null && l.BaseAmount == 1234.56m);
        journal.DomainEvents.OfType<SubledgerEntryPosted>().Single().PostingRuleVersion.ShouldBe(3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    public void PostBusinessEvent_RefusesAmountsThatCannotBalance(double amount) =>
        Journal.PostBusinessEvent(Guid.CreateVersion7(), Event((decimal)amount), Premium, OpenOctober(), Base, Now).Error!.Code.ShouldBe(FinanceErrorCodes.JournalUnbalanced);

    [Fact]
    public void PostBusinessEvent_ForeignCurrency_StoresRateAndBaseAmount()
    {
        var journal = Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(100m) with { Currency = "USD" }, Premium, OpenOctober(), new ExchangeRate("ZAR", 18.255m), Now).Value;

        journal.Lines.ShouldAllBe(l => l.Currency == "USD" && l.FxRate == 18.255m && l.BaseAmount == 1825.50m);
    }

    [Fact]
    public void PostBusinessEvent_OnlyIntoAnOpenPeriod()
    {
        var future = AccountingPeriod.Define(Guid.CreateVersion7(), "2026-10", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(10m), Premium, future, Base, Now).Error!.Code.ShouldBe(FinanceErrorCodes.PeriodNotOpen);

        var soft = OpenOctober();
        soft.SoftClosePeriod(Guid.CreateVersion7(), Now);
        Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(10m), Premium, soft, Base, Now).Error!.Code.ShouldBe(FinanceErrorCodes.PeriodNotOpen);

        var november = Event(10m) with { AccountingDate = new DateOnly(2026, 11, 2) };
        Journal.PostBusinessEvent(Guid.CreateVersion7(), november, Premium, OpenOctober(), Base, Now).Error!.Code.ShouldBe(FinanceErrorCodes.PeriodNotOpen);
    }

    [Fact]
    public void ReverseJournal_SwapsSides_KeepsTheOriginalUnchanged_FIN003()
    {
        var period = OpenOctober();
        var original = Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(500m), Premium, period, Base, Now).Value;
        var lines = original.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).ToList();

        var reversal = Journal.ReverseJournal(Guid.CreateVersion7(), original, alreadyReversed: false, Guid.CreateVersion7(), Day, period, Now).Value;

        reversal.ReversesJournalId.ShouldBe(original.Id);
        reversal.Lines.Select(l => (l.AccountCode, l.Credit, l.Debit)).ShouldBe(lines);
        reversal.Lines.ShouldAllBe(l => l.PostingRuleVersion == 3);
        original.Lines.Select(l => (l.AccountCode, l.Debit, l.Credit)).ShouldBe(lines);
        original.Status.ShouldBe("POSTED");
    }

    [Fact]
    public void ReverseJournal_OnceOnly_AndNeverAReversal()
    {
        var period = OpenOctober();
        var original = Journal.PostBusinessEvent(Guid.CreateVersion7(), Event(500m), Premium, period, Base, Now).Value;
        Journal.ReverseJournal(Guid.CreateVersion7(), original, alreadyReversed: true, Guid.CreateVersion7(), Day, period, Now).Error!.Code
            .ShouldBe(FinanceErrorCodes.AlreadyReversed);

        var reversal = Journal.ReverseJournal(Guid.CreateVersion7(), original, false, Guid.CreateVersion7(), Day, period, Now).Value;
        Journal.ReverseJournal(Guid.CreateVersion7(), reversal, false, Guid.CreateVersion7(), Day, period, Now).Error!.Code.ShouldBe(FinanceErrorCodes.AlreadyReversed);
    }

    [Fact]
    public void PostingRuleResolver_PicksTheOneRuleWhoseConditionHolds()
    {
        List<PostingRule> rules =
        [
            new("PR-SMALL", 1, "CLAIM_PAID", Expression.Parse("amount < 1000").Value, "5000", "1000"),
            new("PR-LARGE", 2, "CLAIM_PAID", Expression.Parse("amount >= 1000").Value, "5100", "1000"),
            Premium,
        ];

        PostingRuleResolver.Resolve(rules, Event(5000m, "CLAIM_PAID", new() { ["amount"] = 5000m })).Value.RuleCode.ShouldBe("PR-LARGE");
        PostingRuleResolver.Resolve(rules, Event(10m, "REFUND_PAID")).Error!.Code.ShouldBe(FinanceErrorCodes.PostingRuleNotFound);
        PostingRuleResolver.Resolve([.. rules, new("PR-ALL", 1, "CLAIM_PAID", null, "5200", "1000")], Event(5m, "CLAIM_PAID", new() { ["amount"] = 5m })).Error!.Code
            .ShouldBe(FinanceErrorCodes.PostingRuleAmbiguous);
    }

    [Fact]
    public void Reopen_NeedsASecondPerson_AndChangesNothingUntilApproved()
    {
        var period = OpenOctober();
        var maker = Guid.CreateVersion7();
        period.SoftClosePeriod(maker, Now);

        period.RequestReopen(maker, Now).IsSuccess.ShouldBeTrue();
        period.Status.ShouldBe(PeriodStatus.SoftClose);
        period.DomainEvents.OfType<PeriodReopenRequested>().ShouldHaveSingleItem();
        period.ClosePeriod(maker, Now).Error!.Code.ShouldBe(FinanceErrorCodes.ReopenPending);

        period.DecideReopen(approved: true, maker).Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        period.Status.ShouldBe(PeriodStatus.SoftClose);

        period.DecideReopen(approved: true, Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        period.Status.ShouldBe(PeriodStatus.Open);
    }

    [Fact]
    public void ClosedPeriod_IsNeverReopened()
    {
        var period = OpenOctober();
        period.SoftClosePeriod(Guid.CreateVersion7(), Now);
        period.ClosePeriod(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        period.RequestReopen(Guid.CreateVersion7(), Now).IsFailure.ShouldBeTrue();
        period.AcceptsPostingOn(Day).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReopenRequest_IsClearedByEveryEndingOtherThanApproval_SRS002(bool rejectedByAChecker)
    {
        var period = OpenOctober();
        var maker = Guid.CreateVersion7();
        period.SoftClosePeriod(maker, Now);
        period.RequestReopen(maker, Now);

        // Expired or withdrawn: no checker (Guid.Empty), so none is compared.
        period.DecideReopen(approved: false, rejectedByAChecker ? Guid.CreateVersion7() : Guid.Empty).IsSuccess.ShouldBeTrue();
        (period.Status, period.ReopenRequestedBy).ShouldBe((PeriodStatus.SoftClose, null));

        // Applied again (a redelivery), it changes nothing.
        period.DecideReopen(approved: false, Guid.Empty).IsSuccess.ShouldBeTrue();
        period.DecideReopen(approved: true, Guid.CreateVersion7()).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ReopenEnding_ThePeriodRefuses_ParksTheDelivery_D5()
    {
        var period = OpenOctober();
        var maker = Guid.CreateVersion7();
        period.SoftClosePeriod(maker, Now);
        period.RequestReopen(maker, Now);
        var policy = new Insys.Finance.Application.PeriodReopenPolicy(new OnePeriod(period));
        var approvalId = Guid.CreateVersion7();
        var envelope = new Insys.BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), Guid.CreateVersion7(), "workflow.ApprovalRejected", 1, Now, "Approval",
            approvalId, Guid.CreateVersion7(), null, default);

        var refused = new Insys.Workflow.Contracts.ApprovalRejected(approvalId, "ACCOUNTING_PERIOD", period.Id, "PERIOD_REOPEN", maker, maker, "Same person");
        var parked = await Should.ThrowAsync<Insys.BuildingBlocks.Application.Messaging.PrerequisiteMissingException>(() =>
            policy.Handle(refused, envelope, TestContext.Current.CancellationToken));
        (parked.Code, parked.Item).ShouldBe(("APPROVAL_ENDING_NOT_APPLIED", $"PERIOD_REOPEN:{approvalId}"));
        period.ReopenRequestedBy.ShouldBe(maker);

        var expired = refused with { CheckerId = Guid.Empty, Ending = Insys.Workflow.Contracts.ApprovalEndings.Expired };
        await policy.Handle(expired, envelope, TestContext.Current.CancellationToken);
        period.ReopenRequestedBy.ShouldBeNull();
    }

    [Fact]
    public async Task ReopenGrant_ThePeriodWillNotTake_ParksTheDelivery_ARedeliveryChangesNothing_D5()
    {
        var period = OpenOctober();
        var maker = Guid.CreateVersion7();
        period.SoftClosePeriod(maker, Now);
        period.RequestReopen(maker, Now);
        var policy = new Insys.Finance.Application.PeriodReopenPolicy(new OnePeriod(period));
        var approvalId = Guid.CreateVersion7();
        var envelope = new Insys.BuildingBlocks.Contracts.EventEnvelope(Guid.CreateVersion7(), Guid.CreateVersion7(), "workflow.ApprovalGranted", 1, Now, "Approval",
            approvalId, Guid.CreateVersion7(), null, default);

        // The requester as checker: the period will not take the grant, and the delivery parks (never only logged).
        var refused = new Insys.Workflow.Contracts.ApprovalGranted(approvalId, "ACCOUNTING_PERIOD", period.Id, "PERIOD_REOPEN", maker, maker);
        var parked = await Should.ThrowAsync<Insys.BuildingBlocks.Application.Messaging.PrerequisiteMissingException>(() =>
            policy.Handle(refused, envelope, TestContext.Current.CancellationToken));
        (parked.Code, parked.Item).ShouldBe(("APPROVAL_NOT_APPLIED", $"PERIOD_REOPEN:{approvalId}"));
        (period.Status, period.ReopenRequestedBy).ShouldBe((PeriodStatus.SoftClose, maker));

        var granted = refused with { CheckerId = Guid.CreateVersion7() };
        await policy.Handle(granted, envelope, TestContext.Current.CancellationToken);
        (period.Status, period.ReopenApplied).ShouldBe((PeriodStatus.Open, true));

        // Delivered again, it changes nothing; another business reference type is not Finance's.
        await policy.Handle(granted, envelope, TestContext.Current.CancellationToken);
        await policy.Handle(granted with { BusinessRefType = "OTHER" }, envelope, TestContext.Current.CancellationToken);
        period.Status.ShouldBe(PeriodStatus.Open);
    }

    private sealed class OnePeriod(AccountingPeriod period) : Insys.Finance.Application.IAccountingPeriods
    {
        public Task<AccountingPeriod?> Covering(DateOnly date, CancellationToken cancellationToken) => Task.FromResult<AccountingPeriod?>(period);

        public Task<AccountingPeriod?> ByCode(string periodCode, CancellationToken cancellationToken) => Task.FromResult<AccountingPeriod?>(period);

        public Task<AccountingPeriod?> Get(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == period.Id ? period : null);

        public Task<IReadOnlyList<AccountingPeriod>> All(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AccountingPeriod>>([period]);

        public void Add(AccountingPeriod added)
        {
        }
    }
}
