/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications.Tests
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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.Communications.Application;
using Insys.Communications.Application.Triggers;
using Insys.Communications.Contracts;
using Insys.Communications.Infrastructure.Triggers;
using Insys.General.Contracts;
using Insys.Health.Contracts;
using Insys.Life.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Communications.Tests;

/// <summary>The event-driven triggers of addenda/communications.md §6: which notice an event asks for, to whom, and with which merge values.</summary>
public sealed class TriggerMappingTests
{
    private static readonly Guid Payer = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid PolicyId = Guid.NewGuid();
    private static readonly Guid ClaimId = Guid.NewGuid();
    private static readonly DateOnly StatusDate = new(2026, 10, 25);
    private static readonly NoticeRecipient Recipient = new(Guid.NewGuid(), "CLM-0001", "MOTOR", "GP-001");

    private const string ReasonText = "Excluded condition: chronic renal failure";

    private static void ShouldUseOnlyAllowedFields(NotificationRequest request)
    {
        var allowed = MergeFields.Allowed(request.TypeCode);
        foreach (var field in request.MergeValues.Keys)
        {
            allowed.ShouldContain(field, $"{field} is not an allowed merge field of {request.TypeCode}");
        }
    }

    private static ArrearsDetected Arrears(string? action, Guid? payer) =>
        new(AccountId, "B30", 31, 1234.5m, "ZAR", "ACC-000123", payer, action);

    // --- Payment reminders (F099, §6.1) ---

    [Theory]
    [InlineData("REMINDER", NotificationTypes.PaymentReminder)]
    [InlineData("FINAL_NOTICE", NotificationTypes.PaymentFinalNotice)]
    public void Arrears_bucket_action_chooses_the_payment_notice_F099(string action, string type)
    {
        var request = PaymentReminderNotices.For(Arrears(action, Payer)).ShouldNotBeNull();

        request.TypeCode.ShouldBe(type);
        request.RecipientPartyId.ShouldBe(Payer);
        request.SourceType.ShouldBe(TriggerSources.BillingAccount);
        request.SourceId.ShouldBe(AccountId);
        request.MergeValues["account_number"].ShouldBe("ACC-000123");
        request.MergeValues["amount_overdue"].ShouldBe("1234.50");
        request.MergeValues["currency"].ShouldBe("ZAR");
        request.MergeValues["days_overdue"].ShouldBe("31");
        ShouldUseOnlyAllowedFields(request);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("LAPSE")]
    [InlineData("CANCEL")]
    [InlineData(null)]
    public void Arrears_with_any_other_action_sends_nothing_F099(string? action) =>
        PaymentReminderNotices.For(Arrears(action, Payer)).ShouldBeNull();

    [Fact]
    public void Arrears_without_a_payer_sends_nothing_F099() =>
        PaymentReminderNotices.For(Arrears("REMINDER", null)).ShouldBeNull();

    // --- Renewals (F021, F100, §6.2) ---

    [Fact]
    public void Renewal_invitation_goes_to_the_policyholder_F021()
    {
        var holder = Guid.NewGuid();
        var request = RenewalNotices.For(new GeneralRenewalInvited(PolicyId, "POL-1", "MOTOR", 2, new DateOnly(2026, 12, 31), "RENEWAL_INV", holder))
            .ShouldNotBeNull();

        request.TypeCode.ShouldBe(NotificationTypes.RenewalInvitation);
        request.RecipientPartyId.ShouldBe(holder);
        request.SourceType.ShouldBe(TriggerSources.GeneralPolicy);
        request.SourceId.ShouldBe(PolicyId);
        request.MergeValues.ShouldBe(new Dictionary<string, string> { ["policy_number"] = "POL-1", ["product_code"] = "MOTOR", ["period_end"] = "2026-12-31" },
            ignoreOrder: true);
        ShouldUseOnlyAllowedFields(request);
    }

    [Fact]
    public void Renewal_invitation_without_a_policyholder_sends_nothing_F021() =>
        RenewalNotices.For(new GeneralRenewalInvited(PolicyId, "POL-1", "MOTOR", 2, new DateOnly(2026, 12, 31), "RENEWAL_INV")).ShouldBeNull();

    [Fact]
    public void Renewal_reminder_adds_the_days_to_renewal_F100()
    {
        var holder = Guid.NewGuid();
        var request = RenewalNotices.For(new GeneralRenewalReminderDue(PolicyId, "POL-1", "MOTOR", new DateOnly(2026, 12, 31), 14, holder, "GP-001"));

        request.TypeCode.ShouldBe(NotificationTypes.RenewalReminder);
        request.RecipientPartyId.ShouldBe(holder);
        request.SourceType.ShouldBe(TriggerSources.GeneralPolicy);
        request.SourceId.ShouldBe(PolicyId);
        request.OrgNodeCode.ShouldBe("GP-001");
        request.MergeValues["period_end"].ShouldBe("2026-12-31");
        request.MergeValues["days_to_renewal"].ShouldBe("14");
        ShouldUseOnlyAllowedFields(request);
    }

    [Fact]
    public void Renewal_reminder_key_is_stable_for_the_same_policy_period_and_day_F100()
    {
        var periodEnd = new DateOnly(2026, 12, 31);

        RenewalNotices.ReminderKey(PolicyId, periodEnd, 14).ShouldBe(RenewalNotices.ReminderKey(PolicyId, periodEnd, 14));
        RenewalNotices.ReminderKey(PolicyId, periodEnd, 14).ShouldNotBe(RenewalNotices.ReminderKey(PolicyId, periodEnd, 7));
        RenewalNotices.ReminderKey(PolicyId, periodEnd, 14).ShouldNotBe(RenewalNotices.ReminderKey(PolicyId, periodEnd.AddYears(1), 14));
        RenewalNotices.ReminderKey(PolicyId, periodEnd, 14).ShouldNotBe(RenewalNotices.ReminderKey(Guid.NewGuid(), periodEnd, 14));
    }

    // --- Claim status (F101, §6.3) ---

    public static TheoryData<string, NotificationRequest, string> ClaimNotices => new()
    {
        { "GeneralRegistered", ClaimStatusNotices.For(new GeneralClaimRegistered(ClaimId, "GC-1", PolicyId, StatusDate, "FIRE"), Recipient, StatusDate), ClaimNoticeStatuses.Registered },
        { "GeneralApproved", ClaimStatusNotices.For(new GeneralClaimApproved(ClaimId, "GC-1", Guid.NewGuid(), Guid.NewGuid(), "hash", 5000m, "ZAR", "INDEMNITY",
            Guid.NewGuid(), Guid.NewGuid()), Recipient, StatusDate), ClaimNoticeStatuses.Approved },
        { "GeneralDeclined", ClaimStatusNotices.For(new GeneralClaimDeclined(ClaimId, "GC-1", "EXCLUDED_PERIL"), Recipient, StatusDate), ClaimNoticeStatuses.Declined },
        { "GeneralSettled", ClaimStatusNotices.For(new GeneralClaimSettled(ClaimId, "GC-1", 4999.9m, "ZAR", StatusDate), Recipient, StatusDate), ClaimNoticeStatuses.Settled },
        { "GeneralClosed", ClaimStatusNotices.For(new GeneralClaimClosed(ClaimId, "GC-1"), Recipient, StatusDate), ClaimNoticeStatuses.Closed },
        { "LifeRegistered", ClaimStatusNotices.For(new LifeClaimRegistered(ClaimId, "GC-1", PolicyId, "DEATH", StatusDate), Recipient, StatusDate), ClaimNoticeStatuses.Registered },
        { "LifeApproved", ClaimStatusNotices.For(new LifeClaimApproved(ClaimId, "GC-1", PolicyId, 100000m, 0m, "ZAR", "CLAIM", [], Guid.NewGuid(), Guid.NewGuid()),
            Recipient, StatusDate), ClaimNoticeStatuses.Approved },
        { "LifeDeclined", ClaimStatusNotices.For(new LifeClaimDeclined(ClaimId, "GC-1", ReasonText), Recipient, StatusDate), ClaimNoticeStatuses.Declined },
        { "LifeSettled", ClaimStatusNotices.For(new LifeClaimSettled(ClaimId, "GC-1", PolicyId, [], 4999.9m, "ZAR", StatusDate), Recipient, StatusDate), ClaimNoticeStatuses.Settled },
    };

    [Theory]
    [MemberData(nameof(ClaimNotices))]
    public void Claim_event_sends_claim_status_with_its_status_F101(string label, NotificationRequest request, string status)
    {
        request.TypeCode.ShouldBe(NotificationTypes.ClaimStatus, label);
        request.RecipientPartyId.ShouldBe(Recipient.PartyId);
        request.SourceType.ShouldBe(label.StartsWith("General", StringComparison.Ordinal) ? TriggerSources.GeneralClaim : TriggerSources.LifeClaim);
        request.SourceId.ShouldBe(ClaimId);
        request.ProductCode.ShouldBe(Recipient.ProductCode);
        request.OrgNodeCode.ShouldBe(Recipient.OrgNodeCode);
        request.MergeValues["claim_number"].ShouldBe("GC-1");
        request.MergeValues["status"].ShouldBe(status);
        request.MergeValues["status_date"].ShouldBe("2026-10-25");
        ShouldUseOnlyAllowedFields(request);
    }

    [Theory]
    [MemberData(nameof(ClaimNotices))]
    public void Claim_amount_appears_only_on_settlement_OQ_086(string label, NotificationRequest request, string status)
    {
        if (status == ClaimNoticeStatuses.Settled)
        {
            request.MergeValues["amount"].ShouldBe("4999.90", label);
            request.MergeValues["currency"].ShouldBe("ZAR", label);
        }
        else
        {
            request.MergeValues.ShouldNotContainKey("amount", label);
            request.MergeValues.ShouldNotContainKey("currency", label);
        }

        request.MergeValues.Values.ShouldNotContain(ReasonText);
    }

    // --- Health decisions and preauthorisations (F031, §6.3) ---

    public static TheoryData<string, NotificationRequest, string> HealthNotices => new()
    {
        { "AdjudicatedPended", HealthDecisionNotices.For(new MedicalClaimAdjudicated(ClaimId, "HC-1", Guid.NewGuid(), true, 0m, "ZAR", [], [new HighCostLine(1, "HC_RULE")]),
            Recipient, StatusDate), HealthNoticeStatuses.Pended },
        { "Adjudicated", HealthDecisionNotices.For(new MedicalClaimAdjudicated(ClaimId, "HC-1", Guid.NewGuid(), false, 750m, "ZAR", [], []), Recipient, StatusDate),
            HealthNoticeStatuses.Adjudicated },
        { "Settled", HealthDecisionNotices.For(new MedicalClaimSettled(ClaimId, "HC-1", "PRV-1", Guid.NewGuid(), 750m, "ZAR", StatusDate), Recipient, StatusDate),
            HealthNoticeStatuses.Paid },
        { "Rejected", HealthDecisionNotices.For(new MedicalClaimRejected(ClaimId, "HC-1", Guid.NewGuid(), ReasonText), Recipient, StatusDate), HealthNoticeStatuses.Rejected },
        { "Reversed", HealthDecisionNotices.For(new MedicalClaimReversed(ClaimId, "HC-1", "PRV-1", 750m, "ZAR", ReasonText), Recipient, StatusDate),
            HealthNoticeStatuses.Reversed },
    };

    [Theory]
    [MemberData(nameof(HealthNotices))]
    public void Medical_claim_event_sends_the_health_decision_with_its_status_F031(string label, NotificationRequest request, string status)
    {
        request.TypeCode.ShouldBe(NotificationTypes.HealthClaimDecision, label);
        request.RecipientPartyId.ShouldBe(Recipient.PartyId);
        request.SourceType.ShouldBe(TriggerSources.HealthClaim);
        request.SourceId.ShouldBe(ClaimId);
        request.MergeValues["claim_number"].ShouldBe("HC-1");
        request.MergeValues["status"].ShouldBe(status);
        ShouldUseOnlyAllowedFields(request);
    }

    [Theory]
    [MemberData(nameof(HealthNotices))]
    public void Health_total_paid_appears_only_when_paid_and_no_reason_or_line_ever_does_HLT_008(string label, NotificationRequest request, string status)
    {
        if (status == HealthNoticeStatuses.Paid)
        {
            request.MergeValues["total_paid"].ShouldBe("750.00", label);
            request.MergeValues["currency"].ShouldBe("ZAR", label);
        }
        else
        {
            request.MergeValues.ShouldNotContainKey("total_paid", label);
            request.MergeValues.ShouldNotContainKey("currency", label);
        }

        foreach (var value in request.MergeValues.Values)
        {
            value.ShouldNotContain("renal", Case.Insensitive, label);
            value.ShouldNotContain("HC_RULE", Case.Sensitive, label);
        }
    }

    [Fact]
    public void Preauthorisation_approved_sends_the_decision_with_its_validity_F031()
    {
        var authId = Guid.NewGuid();
        var request = HealthDecisionNotices.For(new PreauthorisationApproved(authId, "PA-1", Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 30), [new ReservationChange(Guid.NewGuid(), "HOSPITAL", 20000m)]), Recipient);

        request.TypeCode.ShouldBe(NotificationTypes.PreauthDecision);
        request.RecipientPartyId.ShouldBe(Recipient.PartyId);
        request.SourceType.ShouldBe(TriggerSources.HealthPreauth);
        request.SourceId.ShouldBe(authId);
        request.MergeValues.ShouldBe(new Dictionary<string, string>
        {
            ["auth_number"] = "PA-1", ["status"] = HealthNoticeStatuses.Approved, ["valid_from"] = "2026-11-01", ["valid_to"] = "2026-11-30",
        }, ignoreOrder: true);
        ShouldUseOnlyAllowedFields(request);
    }

    [Fact]
    public void Preauthorisation_rejected_sends_the_decision_without_its_reason_F031()
    {
        var authId = Guid.NewGuid();
        var request = HealthDecisionNotices.For(new PreauthorisationRejected(authId, "PA-1", ReasonText), Recipient);

        request.TypeCode.ShouldBe(NotificationTypes.PreauthDecision);
        request.SourceType.ShouldBe(TriggerSources.HealthPreauth);
        request.SourceId.ShouldBe(authId);
        request.MergeValues.ShouldBe(new Dictionary<string, string> { ["auth_number"] = "PA-1", ["status"] = HealthNoticeStatuses.Rejected }, ignoreOrder: true);
        ShouldUseOnlyAllowedFields(request);
    }

    // --- Registration ---

    [Fact]
    public void Every_trigger_has_its_own_inbox_consumer_within_60_characters_INT_005()
    {
        var services = new ServiceCollection().AddNotificationTriggers();
        var consumers = services.Where(d => d.ServiceType == typeof(EventSubscription)).Select(d => ((EventSubscription)d.ImplementationInstance!).Consumer).ToList();

        consumers.Count.ShouldBe(21);
        consumers.Distinct(StringComparer.Ordinal).Count().ShouldBe(21);
        consumers.ShouldAllBe(c => c.Length <= 60);
    }

    /// <summary>OQ-137: a settlement paid less the policy's arrears tells the customer the gross, the premium deducted and the net paid.</summary>
    [Fact]
    public void ASettledNotice_PaidLessTheArrears_ShowsGrossDeductionAndNet_OQ137()
    {
        var merge = ClaimStatusNotices.For(new GeneralClaimSettled(ClaimId, "GC-1", 15_000m, "ZAR", StatusDate, PremiumOffset: 1_250m), Recipient, StatusDate).MergeValues;
        (merge["amount"], merge["premium_deducted"], merge["net_amount"], merge["currency"]).ShouldBe(("15000.00", "1250.00", "13750.00", "ZAR"));
        ClaimStatusNotices.For(new GeneralClaimSettled(ClaimId, "GC-1", 15_000m, "ZAR", StatusDate), Recipient, StatusDate).MergeValues.ContainsKey("premium_deducted")
            .ShouldBeFalse("nothing was deducted");
    }
}
