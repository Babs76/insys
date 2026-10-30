/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using System.Net;
using System.Text.Json;
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Contracts;
using Insys.Integration.Infrastructure.Partners;
using Insys.Party.Contracts;
using Insys.Party.Infrastructure;
using Insys.Tenancy.Contracts;
using Insys.Testing;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Customer due diligence (addenda/due-diligence.md, signed off 29 October 2026; Wave 3 batch 2), §11: screening and the
/// review of hits (F003), a true sanctions match and its unblocking by a second person, a PEP and enhanced due diligence
/// (OQ-126), the gates (OQ-123, §5), ongoing due diligence (OQ-124, OQ-125), risk classes and their override (F077), a
/// payout held for its payee, readiness (§7) and privacy (§3). Each test has a tenant of its own, new since V0092, so its
/// gates are on (it is entitled to LIFE), with a screening and an identity verification partner. The stub screening
/// provider matches names containing SANCTIONLIST, PEPLIST or MEDIALIST.
/// </summary>
public sealed class DueDiligenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly DateOnly Today = TestClock.Today;

    private static readonly string[] Permissions =
    [
        PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate, PartyPermissions.DueDiligenceRead, PartyPermissions.DueDiligenceCheck,
        PartyPermissions.DueDiligenceOverrideRisk, PartyPermissions.DueDiligenceReport, PartyPermissions.DueDiligenceUnblock, PartyPermissions.DueDiligenceApproveEnhanced,
        DocumentPermissions.DocumentCreate, DocumentPermissions.DocumentRead, WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove, TenancyPermissions.Read,
    ];

    [Fact]
    public async Task AScreeningHit_OpensAReviewTask_AndStopsClearance_AFalsePositiveWithAReason_ClearsIt_F003()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Sanctionlist");
        await w.Factory.DrainOutbox();

        // Screened when registered: the hit stops clearance, and compliance gets a review task about the party, which holds no hit.
        var view = await DueDiligence(w.Clerk, party);
        view.GetProperty("status").GetString().ShouldBe("REFERRED");
        Outstanding(view).ShouldContain("HIT_REVIEW");
        view.GetProperty("hits").ValueKind.ShouldBe(JsonValueKind.Null, "the clerk does not hold due_diligence.review_hit");
        (await Running(w, "DUE_DILIGENCE_HIT_REVIEW", party)).ShouldBe(1L);
        (await fixture.Database.ScalarAsOwner<string>(
            "SELECT t.queue_code FROM workflow.process_instance p JOIN workflow.task t ON t.tenant_id = p.tenant_id AND t.process_instance_id = p.id WHERE p.business_ref_id = @p",
            ("p", party))).ShouldBe("COMPLIANCE");
        (await Gate(w, party, DueDiligenceSteps.Payout, "GENERAL")).Outstanding.ShouldContain("HIT_REVIEW", "s26B: no payout with a hit unreviewed");

        var reviewed = await DueDiligence(w.Reviewer, party);
        var hit = reviewed.GetProperty("hits").EnumerateArray().Single();
        (hit.GetProperty("listType").GetString(), hit.GetProperty("review").ValueKind).ShouldBe(("SANCTIONS", JsonValueKind.Null));
        var url = $"/api/v1/party/parties/{party}/due-diligence/hits/{hit.GetProperty("id").GetGuid()}/review";
        (await w.Clerk.Send(HttpMethod.Post, url, new { outcome = "FALSE_POSITIVE", reason = "Different person" }, ifMatch: await ETag(w.Clerk, party))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden, "reviewing hits needs PARTY_REVIEW_SCREENING_HIT");
        (await w.Reviewer.Send(HttpMethod.Post, url, new { outcome = "FALSE_POSITIVE", reason = "" }, ifMatch: await ETag(w.Reviewer, party))).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity, "a false positive needs a reason");
        var cleared = await w.Reviewer.Send(HttpMethod.Post, url, new { outcome = "FALSE_POSITIVE", reason = "Different birth date and nationality" },
            ifMatch: await ETag(w.Reviewer, party));
        cleared.StatusCode.ShouldBe(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();

        view = await DueDiligence(w.Clerk, party);
        view.GetProperty("status").GetString().ShouldBe("IN_PROGRESS", "the hit no longer stops it; identity and address are still needed");
        Outstanding(view).ShouldBe(["IDENTITY", "ADDRESS"], ignoreOrder: true);
        (await Running(w, "DUE_DILIGENCE_HIT_REVIEW", party)).ShouldBe(0L, "the review task closes with the review");
        (await Gate(w, party, DueDiligenceSteps.Payout, "GENERAL")).Cleared.ShouldBeTrue("screened, no hit waiting");

        // F002: the identity (verified by the provider when added) and the address (verified by the clerk against a document) clear it.
        await AddIdentifier(w, party, "7501015009081");
        await Check(w, party, "ADDRESS", await Document(w, party));
        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), view.GetProperty("riskClass").GetString(), view.GetProperty("level").GetString()).ShouldBe(("CLEARED", "LOW", "STANDARD"));
        view.GetProperty("nextReviewOn").GetString().ShouldBe(Today.AddYears(5).ToString("yyyy-MM-dd"), "OQ-124: LOW is reviewed every 5 years");
        (await Gate(w, party, DueDiligenceSteps.Issue, "LIFE")).Cleared.ShouldBeTrue();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.outbox_event WHERE event_type = 'party.DueDiligenceCleared' AND aggregate_id = @p",
            ("p", party))).ShouldBeGreaterThanOrEqualTo(0L);
    }

    [Fact]
    public async Task ATrueSanctionsMatch_BlocksTheParty_NeedsAReport_OpensAComplianceCase_AndIsUnblockedOnlyByASecondPerson_F003()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Sanctionlist Listed");
        await w.Factory.DrainOutbox();
        var hit = (await DueDiligence(w.Reviewer, party)).GetProperty("hits").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/hits/{hit}/review",
            new { outcome = "TRUE_MATCH", reason = "Same name, birth date and nationality" }, ifMatch: await ETag(w.Reviewer, party))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Factory.DrainOutbox();

        var view = await DueDiligence(w.Reviewer, party);
        (view.GetProperty("status").GetString(), view.GetProperty("blocked").GetBoolean()).ShouldBe(("BLOCKED", true));
        view.GetProperty("hits")[0].GetProperty("reportRequired").GetBoolean().ShouldBeTrue("s28A: property of a sanctioned person is reported");
        (await Running(w, "DUE_DILIGENCE_SANCTIONS", party)).ShouldBe(1L, "compliance's case");
        (await Running(w, "DUE_DILIGENCE_HIT_REVIEW", party)).ShouldBe(0L);
        (await Gate(w, party, DueDiligenceSteps.Payout, "GENERAL")).Outstanding.ShouldContain("BLOCKED");

        var reported = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/report", new { hitId = hit, reportType = "S28A", ficReference = "FIC-2026-0042" });
        reported.StatusCode.ShouldBe(HttpStatusCode.OK, await reported.Content.ReadAsStringAsync());
        (await DueDiligence(w.Reviewer, party)).GetProperty("hits")[0].GetProperty("ficReference").GetString().ShouldBe("FIC-2026-0042");

        // Unblocking is privileged and needs a second person: the requester cannot approve it; another holder of the permission can.
        var requested = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/unblock", new { reason = "Delisted by OFAC on review" });
        requested.StatusCode.ShouldBe(HttpStatusCode.OK, await requested.Content.ReadAsStringAsync());
        (await requested.Json()).GetProperty("unblockPending").GetBoolean().ShouldBeTrue();
        await w.Factory.DrainOutbox();
        var approval = await Waiting(w, PartyApprovals.UnblockBusinessRefType, party);
        (await (await w.Clerk.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await DueDiligence(w.Clerk, party)).GetProperty("blocked").GetBoolean().ShouldBeTrue();
        var approved = await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();

        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("blocked").GetBoolean(), view.GetProperty("status").GetString()).ShouldBe((false, "IN_PROGRESS"));
        (await Running(w, "DUE_DILIGENCE_SANCTIONS", party)).ShouldBe(0L);
    }

    [Fact]
    public async Task APep_RaisesTheLevelToEnhanced_AndClearanceNeedsAnApproval_TheMakerCannotGive_OQ126()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Peplist");
        await w.Factory.DrainOutbox();
        await AddIdentifier(w, party, "6802025009087");
        await Check(w, party, "ADDRESS", await Document(w, party));
        var hit = (await DueDiligence(w.Reviewer, party)).GetProperty("hits").EnumerateArray().Single();
        hit.GetProperty("listType").GetString().ShouldBe("PEP");
        (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/hits/{hit.GetProperty("id").GetGuid()}/review",
            new { outcome = "TRUE_MATCH", reason = "A member of a provincial legislature" }, ifMatch: await ETag(w.Reviewer, party))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Factory.DrainOutbox();

        var view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("level").GetString(), view.GetProperty("riskClass").GetString(), view.GetProperty("status").GetString())
            .ShouldBe(("ENHANCED", "HIGH", "IN_PROGRESS"), "F077: the PEP factor's weight gives HIGH; §4.3: a PEP is ENHANCED");
        Outstanding(view).ShouldBe(["SOURCE_OF_FUNDS"]);

        // The source of funds, verified against a document, completes it: clearance waits for PARTY_APPROVE_ENHANCED_DD, and the clerk who completed it is the maker.
        await Check(w, party, "SOURCE_OF_FUNDS", await Document(w, party));
        await w.Factory.DrainOutbox();
        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), Outstanding(view).Single()).ShouldBe(("IN_PROGRESS", "ENHANCED_APPROVAL"));
        var approval = await Waiting(w, PartyApprovals.DueDiligenceBusinessRefType, party);
        (await (await w.Clerk.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code())
            .ShouldBe("MAKER_CHECKER_SAME_PERSON", "BR-109: the reviewer of the PEP hit contributed to the assessment");
        (await w.Approver.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();

        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), view.GetProperty("clearedBy").GetGuid()).ShouldBe(("CLEARED", w.ApproverId));
        view.GetProperty("nextReviewOn").GetString().ShouldBe(Today.AddYears(1).ToString("yyyy-MM-dd"), "OQ-124: HIGH is reviewed every year");
    }

    /// <summary>
    /// BR-109 / GAP-112: when the identity provider's result is the last check to complete, no person is acting, so the
    /// platform requests the enhanced approval (the system requester). It is created, says it was raised by the system, and a
    /// person with authority approves it; the party is cleared.
    /// </summary>
    [Fact]
    public async Task APep_WhoseProvidersCheckFinishesLast_GetsOneEnhancedApproval_RaisedByTheSystem_AskedAgainOnExpiry_NoContributorMayApprove_BR109()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Peplist");
        await w.Factory.DrainOutbox();
        var hit = (await DueDiligence(w.Reviewer, party)).GetProperty("hits").EnumerateArray().Single();
        (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/hits/{hit.GetProperty("id").GetGuid()}/review",
            new { outcome = "TRUE_MATCH", reason = "A member of a provincial legislature" }, ifMatch: await ETag(w.Reviewer, party))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Factory.DrainOutbox();
        await Check(w, party, "ADDRESS", await Document(w, party));
        await Check(w, party, "SOURCE_OF_FUNDS", await Document(w, party));
        var view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("level").GetString(), view.GetProperty("status").GetString()).ShouldBe(("ENHANCED", "IN_PROGRESS"));
        Outstanding(view).ShouldNotContain("ENHANCED_APPROVAL", "the identity check is still missing");

        // The identity provider's result completes it, with no person acting.
        await AddIdentifier(w, party, "6802025009087");
        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), Outstanding(view).Single()).ShouldBe(("IN_PROGRESS", "ENHANCED_APPROVAL"));
        var approval = await Waiting(w, PartyApprovals.DueDiligenceBusinessRefType, party);
        var waiting = await (await w.Reviewer.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json();
        (waiting.GetProperty("makerId").GetGuid(), waiting.GetProperty("raisedBySystem").GetBoolean()).ShouldBe((MakerChecker.SystemMaker, true));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.parked_delivery WHERE tenant_id = @t AND status IN ('FAILING', 'DEAD_LETTERED')",
            ("t", w.Tenant))).ShouldBe(0, "no delivery failed on the way");

        // It expires unanswered: Party asks again, and exactly one enhanced approval is open for the party, raised by the system
        // again. Nobody gets a requester follow-up task, since no person requested it.
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.approval SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", approval));
        await w.Factory.Services.GetRequiredService<Insys.Workflow.Infrastructure.WorkflowJobs>().Run(CancellationToken.None);
        await w.Factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(status, ',' ORDER BY requested_at) FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @p
            """, ("t", w.Tenant), ("r", PartyApprovals.DueDiligenceBusinessRefType), ("p", party))).ShouldBe("EXPIRED,REQUESTED");
        var expired = approval;
        approval = await Waiting(w, PartyApprovals.DueDiligenceBusinessRefType, party);
        approval.ShouldNotBe(expired);
        (await (await w.Approver.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("raisedBySystem").GetBoolean().ShouldBeTrue();
        (await Running(w, "APPROVAL_FOLLOW_UP", party)).ShouldBe(0);
        (await DueDiligence(w.Clerk, party)).GetProperty("status").GetString().ShouldBe("IN_PROGRESS");

        // BR-109 (Ade, 30 October 2026): nobody who contributed may approve it, whatever order the results came in: not the clerk who
        // recorded the source of funds, nor the reviewer of the PEP hit. A compliance officer who took no part does.
        foreach (var contributor in new[] { w.Clerk, w.Reviewer })
        {
            (await (await contributor.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        }

        (await w.Approver.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();
        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), view.GetProperty("clearedBy").GetGuid()).ShouldBe(("CLEARED", w.ApproverId));
    }

    /// <summary>
    /// BR-109: a request Workflow cannot create (here, one with no requester at all) is never lost. Its delivery fails, is
    /// recorded as failing in the delivery ledger, which operations see in the parked deliveries view, and is retried with
    /// back-off until it is dead-lettered.
    /// </summary>
    [Fact]
    public async Task AnApprovalRequestThatCannotBeCreated_FailsItsDelivery_AndIsListedForOperations_NotLost_BR109()
    {
        var w = await World();
        var party = Guid.CreateVersion7();
        var requested = new BuildingBlocks.Contracts.Approvals.ApprovalRequested(PartyApprovals.DueDiligenceBusinessRefType, party, PartyApprovals.EnhancedDueDiligenceType,
            Guid.Empty, null, null, "Clear enhanced due diligence", null, PartyPermissions.DueDiligenceApproveEnhanced);
        var envelope = new EventEnvelope(Guid.CreateVersion7(), w.Tenant, "workflow.ApprovalRequested", 1, DateTimeOffset.UtcNow, "DueDiligence", party, Guid.CreateVersion7(),
            null, JsonSerializer.SerializeToElement(requested, JsonSerializerOptions.Web));
        // The delivery fails (Workflow refuses an approval with no requester): the bus says so, and the ledger keeps it to retry.
        (await Should.ThrowAsync<AggregateException>(() => w.Factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None)))
            .InnerExceptions.Single().ShouldBeOfType<ArgumentException>();

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE tenant_id = @t AND business_ref_id = @p", ("t", w.Tenant), ("p", party)))
            .ShouldBe(0);
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT status || ' ' || failures FROM integration.parked_delivery WHERE tenant_id = @t AND event_id = @e AND consumer LIKE 'workflow.%RequestApprovalPolicy%'
            """, ("t", w.Tenant), ("e", envelope.EventId))).ShouldBe("FAILING 1", "failed, to be retried with back-off");

        await fixture.Database.UserWithPermissions(w.Tenant, "dd-ops", Integration.Contracts.IntegrationPermissions.DeadLetterRead);
        var listed = await (await w.Factory.ClientFor(w.Tenant, "dd-ops").GetAsync("/api/v1/integration/parked-deliveries?status=Failing")).Json();
        listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("eventType").GetString()).ShouldContain("workflow.ApprovalRequested");
    }

    /// <summary>BR-109: the system requester is reserved: no user account may have its ID (V0097), so it cannot be given a role, sign in or decide.</summary>
    [Fact]
    public async Task TheSystemRequester_IsReserved_NoUserAccountMayHaveIt_BR109()
    {
        var w = await World();
        var user = await fixture.Database.UserWithPermissions(w.Tenant, "dd-plain-" + Guid.NewGuid().ToString("N")[..8]);
        var refused = await Should.ThrowAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsOwner(
            "UPDATE iam.user_account SET id = @s WHERE tenant_id = @t AND id = @u", ("s", MakerChecker.SystemMaker), ("t", w.Tenant), ("u", user)));
        (refused.SqlState, refused.ConstraintName).ShouldBe(("23514", "ck_user_account_not_system_requester"));
    }

    [Fact]
    public async Task TheGates_FollowTheSignedOffDefaults_AndAreOffForATenantThatExistedBefore_OQ123()
    {
        var w = await World();
        var unscreened = Guid.CreateVersion7();
        var member = await w.Clerk.RegisterPerson("Member");
        await w.Factory.DrainOutbox();

        // §5 defaults: quotes allowed; Life issue needs cleared due diligence; payouts need a sanctions screening in the last 30 days with no hit waiting.
        (await Gate(w, unscreened, DueDiligenceSteps.Quote, "LIFE")).Cleared.ShouldBeTrue();
        (await Gate(w, unscreened, DueDiligenceSteps.Issue, "GENERAL")).Cleared.ShouldBeTrue();
        var issue = await Gate(w, unscreened, DueDiligenceSteps.Issue, "LIFE");
        (issue.Cleared, issue.Outstanding.Single()).ShouldBe((false, "NOT_STARTED"));
        (await Gate(w, unscreened, DueDiligenceSteps.Payout, DueDiligenceSteps.AnyDomain)).Outstanding.ShouldBe(["SCREENING"]);
        (await Gate(w, member, DueDiligenceSteps.ClaimPayout, "HEALTH")).Cleared.ShouldBeTrue("a screened member with no hit is paid by Health as before");
        (await Gate(w, member, DueDiligenceSteps.Payout, DueDiligenceSteps.AnyDomain)).Cleared.ShouldBeTrue();
        var life = await Gate(w, member, DueDiligenceSteps.ClaimPayout, "LIFE");
        life.Cleared.ShouldBeFalse("Life needs the payee's CDD cleared");
        life.Outstanding.ShouldBe(["IDENTITY", "ADDRESS"], ignoreOrder: true);

        // A screening older than the gate allows is not enough (30 days).
        await fixture.Database.ExecuteAsOwner("UPDATE party.due_diligence SET last_screened_at = now() - interval '31 days' WHERE party_id = @p", ("p", member));
        (await Gate(w, member, DueDiligenceSteps.ClaimPayout, "HEALTH")).Outstanding.ShouldBe(["SCREENING"]);

        // A tenant from before V0092 has its gates off (§5): everything is cleared.
        (await Gate(w with { Tenant = DatabaseFixture.TenantA }, unscreened, DueDiligenceSteps.Issue, "LIFE")).Cleared.ShouldBeTrue();
    }

    [Fact]
    public async Task APassedReviewDate_ExpiresTheParty_TheJobRaisesTheReview_AndAFreshScreeningClearsItAgain_OQ124()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Reviewed");
        await w.Factory.DrainOutbox();
        await AddIdentifier(w, party, "7708085009086");
        await Check(w, party, "ADDRESS", await Document(w, party));
        (await DueDiligence(w.Clerk, party)).GetProperty("status").GetString().ShouldBe("CLEARED");
        await fixture.Database.ExecuteAsOwner("UPDATE party.due_diligence SET next_review_on = @d WHERE party_id = @p", ("d", Today.AddDays(-1)), ("p", party));

        var run = await w.Factory.Services.GetRequiredService<DueDiligenceJobs>().RunForTenant(w.Tenant, CancellationToken.None);
        run!.Expired.ShouldBe(1);
        await w.Factory.DrainOutbox();
        (await DueDiligence(w.Clerk, party)).GetProperty("status").GetString().ShouldBe("EXPIRED");
        (await Running(w, "DUE_DILIGENCE_REVIEW", party)).ShouldBe(1L);
        (await Gate(w, party, DueDiligenceSteps.Issue, "LIFE")).Cleared.ShouldBeFalse("an expired party is not cleared");

        var screened = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/checks", new { checkType = "SCREENING" }, idempotent: true);
        screened.StatusCode.ShouldBe(HttpStatusCode.OK, await screened.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
        var view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("status").GetString(), view.GetProperty("nextReviewOn").GetString()).ShouldBe(("CLEARED", Today.AddYears(5).ToString("yyyy-MM-dd")));
        (await Running(w, "DUE_DILIGENCE_REVIEW", party)).ShouldBe(0L);
    }

    [Fact]
    public async Task AProvidersListUpdate_ScreensAgainThePartiesScreenedOnTheOlderList_OQ125()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Relisted");
        await w.Factory.DrainOutbox();
        (await ListVersion(party)).ShouldBe(StubScreeningLists.InitialVersion);
        var jobs = w.Factory.Services.GetRequiredService<DueDiligenceJobs>();
        (await jobs.RunForTenant(w.Tenant, CancellationToken.None))!.Rescreened.ShouldBe(0, "screened on today's lists");

        StubScreeningLists.Update(w.Tenant, "STUB-2");
        (await jobs.RunForTenant(w.Tenant, CancellationToken.None))!.Rescreened.ShouldBe(1);
        (await ListVersion(party)).ShouldBe("STUB-2");
    }

    [Fact]
    public async Task RiskFactors_GiveTheConfiguredClass_AndAnOverrideNeedsASecondPersonAndIsAudited_F077()
    {
        var w = await World();

        // The tenant's own factors replace the platform's: a South African resident scores 70, which is HIGH.
        await fixture.Database.SeedStatic(w.Tenant, "party.cfg_customer_risk_factors", "EFFECTIVE", "ZA-TEST", Today.AddDays(-1), ("factor", "COUNTRY"),
            ("match_values", new[] { "ZA" }), ("weight", 70));
        var party = await w.Clerk.RegisterPerson("Classified");
        await w.Factory.DrainOutbox();
        var view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("riskScore").GetInt32(), view.GetProperty("riskClass").GetString(), view.GetProperty("level").GetString()).ShouldBe((70, "HIGH", "ENHANCED"));

        var asked = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/risk-class", new { riskClass = "MEDIUM", reason = "Long-standing local client" });
        asked.StatusCode.ShouldBe(HttpStatusCode.OK, await asked.Content.ReadAsStringAsync());
        (await asked.Json()).GetProperty("pendingOverrideClass").GetString().ShouldBe("MEDIUM");
        await w.Factory.DrainOutbox();
        (await DueDiligence(w.Clerk, party)).GetProperty("riskClass").GetString().ShouldBe("HIGH", "it applies once approved");
        var approval = await Waiting(w, PartyApprovals.RiskOverrideBusinessRefType, party);
        (await (await w.Clerk.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await w.Factory.DrainOutbox();

        view = await DueDiligence(w.Clerk, party);
        (view.GetProperty("riskClass").GetString(), view.GetProperty("riskClassOverridden").GetBoolean(), view.GetProperty("level").GetString())
            .ShouldBe(("MEDIUM", true, "STANDARD"));
        foreach (var operation in new[] { "OverrideRiskClass", "ApproveDueDiligence", "ReadDueDiligence" })
        {
            (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = @o", ("t", w.Tenant), ("o", operation)))
                .ShouldBeGreaterThan(0L, operation);
        }
    }

    [Fact]
    public async Task ARefundToAPayeeWithAHitWaiting_IsHeldOutOfTheBatches_AndReleasedOnceTheHitIsCleared_OQ123()
    {
        var w = await World();
        await fixture.Database.SeedStatic(w.Tenant, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", Today.AddDays(-30),
            ("provider_code", "SIMULATOR"), ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "EFT" }), ("credential_ref", "vault:simulator"));
        await fixture.Database.SeedStatic(w.Tenant, "payment.ref_payment_methods", "ACTIVE", "EFT", Today.AddDays(-30), ("method_code", "EFT"), ("direction", "PAY"),
            ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        var payee = await w.Clerk.RegisterPerson("Sanctionlist Refund");
        await w.Factory.DrainOutbox();

        var refund = Guid.CreateVersion7();
        var approved = new EventEnvelope(Guid.CreateVersion7(), w.Tenant, "billing.RefundApproved", 1, DateTimeOffset.UtcNow, "Refund", refund, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(new RefundApproved(refund, Guid.CreateVersion7(), payee, Guid.CreateVersion7(), 210.00m, "ZAR"), JsonSerializerOptions.Web));
        await w.Factory.Services.GetRequiredService<IEventBus>().Deliver(approved, CancellationToken.None);
        await w.Factory.DrainOutbox();
        (await Held(refund)).ShouldBe("DUE_DILIGENCE_NOT_CLEARED", "held, not refused: the payment exists and waits outside the batches");

        var hit = (await DueDiligence(w.Reviewer, payee)).GetProperty("hits").EnumerateArray().Single().GetProperty("id").GetGuid();
        (await w.Reviewer.Send(HttpMethod.Post, $"/api/v1/party/parties/{payee}/due-diligence/hits/{hit}/review", new { outcome = "FALSE_POSITIVE", reason = "Not the same person" },
            ifMatch: await ETag(w.Reviewer, payee))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Factory.DrainOutbox();
        (await Held(refund)).ShouldBeNull("released for the next batch once the payee is cleared for a payout");
    }

    [Fact]
    public async Task Readiness_FlagsALifeTenantWithItsGatesOff_AndAGatedTenantWithoutAProviderOrAReviewer_OQ123()
    {
        var gated = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(gated, "DDR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), dueDiligenceGates: true);
        await fixture.Database.UserWithPermissions(gated, "dd-ready", TenancyPermissions.Read);
        var factory = await fixture.Host();
        var checks = await Checks(factory.ClientFor(gated, "dd-ready"));
        checks["DUE_DILIGENCE_LIFE_GATE"].ShouldBeEmpty("a new LIFE tenant has its gates on");
        checks["DUE_DILIGENCE_PROVIDER"].ShouldBe(["SCREENING", "IDENTITY_VERIFICATION"]);
        checks["DUE_DILIGENCE_REVIEWER"].ShouldBe(["PARTY_REVIEW_SCREENING_HIT"]);

        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "dd-ready-b", TenancyPermissions.Read);
        checks = await Checks(factory.ClientFor(DatabaseFixture.TenantB, "dd-ready-b"));
        checks["DUE_DILIGENCE_LIFE_GATE"].ShouldBe(["GATES_OFF"], "a tenant from before V0092 starts with its gates off, and readiness flags it");
        checks["DUE_DILIGENCE_PROVIDER"].ShouldBeEmpty("with the gates off, nothing needs a provider");
    }

    [Fact]
    public async Task NoEventTaskOrAuditEntry_CarriesAHitsListNameOrScore_AndAPortalSchemaHasNoDueDiligence_DueDiligence3()
    {
        var w = await World();
        var party = await w.Clerk.RegisterPerson("Sanctionlist Private");
        await w.Factory.DrainOutbox();
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM party.outbox_event WHERE tenant_id = @t AND (payload::text ILIKE '%sanctionlist%' OR payload::text ILIKE '%STUB SANCTIONS%' OR payload::text LIKE '%92.5%')",
                     "SELECT count(*) FROM workflow.outbox_event WHERE tenant_id = @t AND (payload::text ILIKE '%sanctionlist%' OR payload::text ILIKE '%STUB SANCTIONS%')",
                     "SELECT count(*) FROM workflow.approval WHERE tenant_id = @t AND reason ILIKE '%sanctionlist%'",
                     "SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND (details::text ILIKE '%STUB SANCTIONS%' OR details::text LIKE '%92.5%')",
                     "SELECT count(*) FROM integration.external_call_log WHERE tenant_id = @t AND row_to_json(external_call_log)::text ILIKE '%sanctionlist%'",
                 })
        {
            (await fixture.Database.ScalarAsOwner<long>(sql, ("t", w.Tenant))).ShouldBe(0L, sql);
        }

        // §6: no portal operation's documented response has a due diligence field.
        var portal = DocumentedSchema.PortalResponseProperties();
        foreach (var field in new[] { "riskClass", "riskScore", "riskClassOverridden", "hits", "ficReference", "lastScreenedAt", "nextReviewOn", "dueDiligence", "heldReason" })
        {
            portal.ShouldNotContain(field);
        }
    }

    private sealed record DdWorld(InsysApiFactory Factory, Guid Tenant, HttpClient Clerk, HttpClient Reviewer, Guid ReviewerId, HttpClient Approver, Guid ApproverId);

    private async Task<DdWorld> World()
    {
        var factory = await fixture.Host();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = Guid.CreateVersion7();
        var db = fixture.Database;
        await db.SeedTenant(tenant, "DD" + suffix.ToUpperInvariant(), dueDiligenceGates: true);
        var clerk = await db.UserWithPermissions(tenant, $"dd-clerk-{suffix}", Permissions);
        var reviewer = await db.UserWithPermissions(tenant, $"dd-review-{suffix}", [.. Permissions, PartyPermissions.DueDiligenceReviewHit]);
        // BR-109: a compliance officer who took no part in the assessment, for the enhanced approval.
        var approver = await db.UserWithPermissions(tenant, $"dd-approve-{suffix}", Permissions);
        var officer = await db.RoleWithPermissions(tenant);
        await db.AssignRole(tenant, clerk, officer);
        await db.AssignRole(tenant, reviewer, officer);
        await db.AssignRole(tenant, approver, officer);
        foreach (var type in new[] { PartyApprovals.EnhancedDueDiligenceType, PartyApprovals.RiskClassOverrideType, PartyApprovals.UnblockType })
        {
            await db.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-{type}", Today.AddYears(-1), ("role_code", officer),
                ("transaction_type", type), ("limit_value", 1_000_000m), ("delegation_allowed", false));
        }

        foreach (var (partner, type) in new[] { ("SCR-1", "SCREENING"), ("IDV-1", "IDENTITY_VERIFICATION") })
        {
            await db.SeedStatic(tenant, "integration.mst_partner_register", "ACTIVE", partner, Today.AddDays(-1), ("partner_code", partner), ("partner_type", type),
                ("legal_name", $"{type} (stub)"), ("status", "ACTIVE"));
        }

        await db.ExecuteAsOwner("""
            INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
            VALUES (@t, 'ZA-NID', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$')
            """, ("t", tenant));
        await db.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "KYC", Today.AddDays(-1), ("category_code", "KYC"), ("label", "Due diligence evidence"),
            ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        return new DdWorld(factory, tenant, factory.ClientFor(tenant, $"dd-clerk-{suffix}"), factory.ClientFor(tenant, $"dd-review-{suffix}"), reviewer,
            factory.ClientFor(tenant, $"dd-approve-{suffix}"), approver);
    }

    private static async Task<JsonElement> DueDiligence(HttpClient client, Guid party)
    {
        var response = await client.GetAsync($"/api/v1/party/parties/{party}/due-diligence");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static async Task<string> ETag(HttpClient client, Guid party) => (await client.GetAsync($"/api/v1/party/parties/{party}/due-diligence")).ETag();

    private static List<string> Outstanding(JsonElement view) => [.. view.GetProperty("outstanding").EnumerateArray().Select(o => o.GetString()!)];

    private static async Task<DueDiligenceAnswer> Gate(DdWorld w, Guid party, string step, string domain)
    {
        await using var scope = w.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(w.Tenant, "test");
        return await scope.ServiceProvider.GetRequiredService<IDueDiligenceStatus>().ClearedFor(w.Tenant, party, step, domain, CancellationToken.None);
    }

    private Task<long> Running(DdWorld w, string process, Guid party) =>
        fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM workflow.process_instance WHERE tenant_id = @t AND process_code = @c AND business_ref_id = @p AND ended_at IS NULL",
            ("t", w.Tenant), ("c", process), ("p", party));

    private Task<Guid> Waiting(DdWorld w, string businessRefType, Guid party) =>
        fixture.Database.ScalarAsOwner<Guid>(
            "SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = @r AND business_ref_id = @p AND status = 'REQUESTED'",
            ("t", w.Tenant), ("r", businessRefType), ("p", party));

    private Task<string?> ListVersion(Guid party) =>
        fixture.Database.ScalarAsOwner<string>("SELECT screened_list_version FROM party.due_diligence WHERE party_id = @p", ("p", party));

    private Task<string?> Held(Guid refund) =>
        fixture.Database.ScalarAsOwner<string>("SELECT held_reason FROM payment.payment WHERE idempotency_key = @k", ("k", $"REFUND:{refund:N}"));

    private static async Task AddIdentifier(DdWorld w, Guid party, string number)
    {
        var etag = (await w.Clerk.GetAsync($"/api/v1/party/parties/{party}")).ETag();
        var added = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number }, ifMatch: etag);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
    }

    private static async Task<Guid> Document(DdWorld w, Guid party)
    {
        var stored = await w.Clerk.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "KYC", businessRefType = "PARTY", businessRefId = party, mimeType = "text/plain", contentBase64 = Convert.ToBase64String("evidence"u8.ToArray()),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        return (await stored.Json()).GetProperty("id").GetGuid();
    }

    private static async Task Check(DdWorld w, Guid party, string checkType, Guid document)
    {
        var checkedNow = await w.Clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/due-diligence/checks", new { checkType, documentId = document }, idempotent: true);
        checkedNow.StatusCode.ShouldBe(HttpStatusCode.OK, await checkedNow.Content.ReadAsStringAsync());
        await w.Factory.DrainOutbox();
    }

    private static async Task<Dictionary<string, List<string>>> Checks(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/tenancy/readiness");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Json()).GetProperty("steps").EnumerateArray().SelectMany(s => s.GetProperty("checks").EnumerateArray())
            .Where(c => c.GetProperty("code").GetString()!.StartsWith("DUE_DILIGENCE_", StringComparison.Ordinal))
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).ToList());
    }
}
