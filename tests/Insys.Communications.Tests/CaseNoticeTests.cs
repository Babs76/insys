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
using Insys.Communications.Application;
using Insys.Communications.Application.Triggers;
using Insys.Communications.Contracts;
using Insys.Workflow.Contracts;

namespace Insys.Communications.Tests;

/// <summary>The case notices and CLAIM_DECISION_RIGHTS (addenda/cases.md §7, OQ-102): their merge values and the fields a template may use.</summary>
public sealed class CaseNoticeTests
{
    [Fact]
    public void ACaseNotice_TakesItsValuesFromTheCase_LeavingOutWhatTheCaseHasNot_Cases7()
    {
        var caseId = Guid.CreateVersion7();
        var party = Guid.CreateVersion7();
        var data = new CaseNoticeData("CS-9", "CLAIM_DISPUTE", CaseFamilies.Dispute, new DateTimeOffset(2026, 12, 1, 22, 30, 0, TimeSpan.Zero), "Reasons", null,
            "NFO", new DateTimeOffset(2027, 1, 30, 9, 0, 0, TimeSpan.Zero), party, null, null);

        var request = CaseNotices.For(NotificationTypes.CaseDecision, caseId, data, party, at => DateOnly.FromDateTime(at.UtcDateTime.AddHours(2)));

        (request.TypeCode, request.RecipientPartyId, request.SourceType, request.SourceId).ShouldBe((NotificationTypes.CaseDecision, party, TriggerSources.Case, caseId));
        request.MergeValues["case_number"].ShouldBe("CS-9");
        request.MergeValues["expected_date"].ShouldBe("2026-12-02", "the business date of the expected time");
        request.MergeValues["representations_deadline"].ShouldBe("2027-01-30");
        request.MergeValues.ContainsKey("review_options").ShouldBeFalse();
    }

    [Fact]
    public void TheCaseAndRightsTypes_AreServiceNotices_OfferingOnlyTheirFields_Cases7_OQ102()
    {
        foreach (var type in new[] { NotificationTypes.CaseAcknowledged, NotificationTypes.CaseUpdate, NotificationTypes.CaseDecision })
        {
            MergeFields.IsPlatformType(type).ShouldBeTrue(type);
            MergeFields.Allowed(type).ShouldContain("ombud_details");
            MergeFields.Allowed(type).ShouldNotContain("opt_out_contact");
        }

        MergeFields.IsPlatformType(NotificationTypes.ClaimDecisionRights).ShouldBeTrue();
        MergeFields.Allowed(NotificationTypes.ClaimDecisionRights).ShouldContain("decision_reasons");
        MergeFields.Allowed(NotificationTypes.ClaimDecisionRights).ShouldNotContain("case_number");
    }
}
