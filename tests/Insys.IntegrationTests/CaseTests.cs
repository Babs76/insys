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
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Workflow.Application;
using Insys.Workflow.Contracts;
using Insys.Workflow.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Cases (docs/specs/addenda/cases.md §12): a complaint through acknowledgement, extension, rejection, internal review and
/// the ombud, with the complaints report; reportability (the 23:30 UTC timeline case is CaseTimelineBoundaryTests); portal raising and
/// scope for each audience (OQ-108); redress approved by another person (OQ-104); legal hold and retention (OQ-106).
/// </summary>
public sealed class CaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Cases = "/api/v1/workflow/cases";
    private const string Portal = "/api/v1/portal/cases";
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly Guid T = DatabaseFixture.TenantA;

    private static readonly string[] Staff =
    [
        CasePermissions.Read, CasePermissions.Create, CasePermissions.Update, CasePermissions.Decide, CasePermissions.Redress, CasePermissions.ReportRead,
        WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;
    private HttpClient _reviewer = null!;
    private Guid _clerkId;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        var db = fixture.Database;
        _clerkId = await db.UserWithPermissions(T, "case-clerk", Staff);
        await db.UserWithPermissions(T, "case-reviewer", Staff);
        await db.UserWithPermissions(T, "case-health", [.. Staff, Health.Contracts.HealthPermissions.ClaimRead]);
        _clerk = _factory.ClientFor(T, "case-clerk");
        _reviewer = _factory.ClientFor(T, "case-reviewer");
        await SeedConfiguration(db);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static async Task SeedConfiguration(TestDatabase db)
    {
        await db.ExecuteAsOwner("""
            INSERT INTO workflow.cfg_workflow_definitions (tenant_id, code, version, effective_from, record_status, created_by, change_reason, process_code, states,
                                                           transitions, domain_validation)
            VALUES (@t, 'CASE_PROC', 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', 'CASE_PROC', ARRAY['HANDLE','DONE'],
                    '[{"from":"HANDLE","to":"DONE"}]', '')
            ON CONFLICT DO NOTHING
            """, ("t", T));
        await Reference(db, "workflow.ref_business_calendars", "CAL-ZA", ("calendar_code", "CAL-ZA"), ("working_days", new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }),
            ("hours", "08:00-17:00"), ("holiday_calendar", ""), ("time_zone", "Africa/Johannesburg"));
        await Reference(db, "workflow.ref_ombud_schemes", "NFO", ("scheme_code", "NFO"), ("name", "National Financial Ombud Scheme"), ("contact", "info@nfosa.co.za"),
            ("referral_information", "Refer within six months of our final decision."), ("families", new[] { "COMPLAINT", "APPEAL" }));
        await Reference(db, "workflow.ref_case_categories", "RPT", ("category_code", "RPT"), ("family", "COMPLAINT"), ("ppr_category", "SERVICE"), ("name", "Report test"));
        await Type(db, "COMPLAINT_T", "COMPLAINT", ["POLICY", "CLAIM", "INVOICE"], ["CUSTOMER", "BROKER"], true, "REVIEW_T");
        await Type(db, "REVIEW_T", "APPEAL", ["POLICY", "CLAIM"], ["CUSTOMER"], false, null);
        await Type(db, "ENQUIRY_T", "ENQUIRY", ["POLICY", "PARTY"], ["CUSTOMER", "BROKER"], false, null);
        await Type(db, "DISPUTE_P", "DISPUTE", ["REMITTANCE"], ["PROVIDER"], false, null);
        await Timeline(db, "COMPLAINT_T", "ACKNOWLEDGE", "P2D", "BUSINESS", "CAL-ZA", false, null);
        await Timeline(db, "COMPLAINT_T", "RESOLVE", "P30D", "CALENDAR", null, true, "P10D");
        await Timeline(db, "COMPLAINT_T", "DECIDE_REPRESENTATIONS", "P45D", "CALENDAR", null, false, null);
        await Timeline(db, "REVIEW_T", "REVIEW", "P10D", "BUSINESS", "CAL-ZA", false, null);
        await Seed(db, "workflow.cfg_case_reportability", "COMPLAINT_T", ("case_type_code", "COMPLAINT_T"), ("query_window", "P5D"));
        var checker = await db.RoleWithPermissions(T);
        await db.AssignRole(T, await db.UserWithPermissions(T, "case-reviewer"), checker);
        await Seed(db, "workflow.cfg_delegation_and_approval_authority", "CASE_REDRESS-" + checker, ("role_code", checker), ("transaction_type", "CASE_REDRESS"),
            ("limit_value", 100_000m), ("delegation_allowed", false), ("currency", "ZAR"));
    }

    private static Task Type(TestDatabase db, string code, string family, string[] links, string[] audiences, bool requiresCategory, string? review) =>
        Seed(db, "workflow.cfg_case_types", code, ("case_type_code", code), ("family", family), ("process_code", "CASE_PROC"), ("default_queue_code", "CASES"),
            ("link_types", links), ("portal_audiences", audiences), ("requires_category", requiresCategory), ("review_case_type_code", (object?)review ?? DBNull.Value),
            ("appeal_route", "PPR_NFO"), ("ombud_scheme_codes", new[] { "NFO" }));

    private static Task Timeline(TestDatabase db, string type, string step, string duration, string basis, string? calendar, bool extensionAllowed, string? max) =>
        Seed(db, "workflow.cfg_case_timelines", $"{type}-{step}", ("case_type_code", type), ("step", step), ("duration", duration), ("basis", basis),
            ("calendar_code", (object?)calendar ?? DBNull.Value), ("extension_allowed", extensionAllowed), ("max_extension", (object?)max ?? DBNull.Value));

    private static Task Seed(TestDatabase db, string table, string code, params (string Column, object Value)[] fields) =>
        db.SeedStatic(T, table, "EFFECTIVE", code, From, fields);

    private static Task Reference(TestDatabase db, string table, string code, params (string Column, object Value)[] fields) =>
        db.SeedStatic(T, table, "ACTIVE", code, From, fields);

    [Fact]
    public async Task AComplaint_IsAcknowledgedExtendedRejectedReviewedByAnotherPerson_AndGoesToTheOmbud_Cases12()
    {
        var policy = Guid.CreateVersion7();
        var opened = await Open(_clerk, new
        {
            caseTypeCode = "COMPLAINT_T", receivedChannel = "PHONE", complainantPartyId = Guid.CreateVersion7(), categoryCode = "RPT", summary = "My claim was ignored",
            links = new[] { new { linkType = "POLICY", recordId = policy, reference = "GP-1" } },
        });
        var id = opened.GetProperty("id").GetGuid();
        opened.GetProperty("caseNumber").GetString()!.ShouldMatch(@"^CASE-\d{6}$");
        opened.GetProperty("status").GetString().ShouldBe("Received");
        opened.GetProperty("expectedBy").ValueKind.ShouldBe(JsonValueKind.String, "the ACKNOWLEDGE timeline gives the expected date");
        opened.GetProperty("processInstanceId").ValueKind.ShouldBe(JsonValueKind.String, "the case type's process runs the case");
        var received = opened.GetProperty("receivedAt").GetDateTimeOffset();
        (await fixture.Database.ScalarAsOwner<string>("SELECT t.queue_code FROM workflow.task t JOIN workflow.service_case c ON c.process_instance_id = t.process_instance_id WHERE c.id = @id",
            ("id", id))).ShouldBe("CASES");

        (await Act(_clerk, id, "acknowledge")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var acknowledged = await Get(_clerk, id);
        acknowledged.GetProperty("expectedBy").GetDateTimeOffset().ShouldBe(received.AddDays(30), "RESOLVE: 30 calendar days from receipt");

        (await Act(_clerk, id, "extend", new { extendBy = "P5D", reason = "Awaiting the assessor's report" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await Act(_clerk, id, "extend", new { extendBy = "P6D", reason = "Again" })).Code()).ShouldBe("CASE_TIMELINE_NOT_EXTENDABLE", "at most P10D in all");
        (await Get(_clerk, id)).GetProperty("expectedBy").GetDateTimeOffset().ShouldBe(received.AddDays(35));

        (await (await Act(_clerk, id, "decide", new { outcome = "REJECTED" })).Code()).ShouldBe("CASE_REASONS_REQUIRED");
        (await Act(_clerk, id, "decide", new { outcome = "REJECTED", reasons = "The loss is excluded under clause 4." })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The internal review (PPR 18.6): an appeal of the decision, decided by someone other than who decided the complaint.
        var appeal = await Open(_clerk, new { caseTypeCode = "REVIEW_T", receivedChannel = "LETTER", complainantPartyId = Guid.CreateVersion7(), summary = "Review please", parentCaseId = id });
        var appealId = appeal.GetProperty("id").GetGuid();
        (await Act(_clerk, appealId, "acknowledge")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var same = await Act(_clerk, appealId, "decide", new { outcome = "UPHELD" });
        same.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await same.Code()).ShouldBe("CASE_DECIDER_SAME_PERSON");
        await fixture.Database.UserWithFunctions(T, "case-no-appeal", "SERVICE_COMPLAINTS", "CASE_DECIDE");
        var notGranted = await Act(_factory.ClientFor(T, "case-no-appeal"), appealId, "decide", new { outcome = "REJECTED", reasons = "No" });
        notGranted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await notGranted.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
        (await Act(_reviewer, appealId, "decide", new { outcome = "REJECTED", reasons = "The decision stands." })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The ombud: the case stays open for the outcome.
        (await (await Act(_clerk, id, "ombud-referral", new { schemeCode = "OTHER" })).Code()).ShouldBe("VALIDATION_FAILED");
        (await Act(_clerk, id, "ombud-referral", new { schemeCode = "NFO", ombudReference = "NFO/2026/1" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Act(_clerk, id, "close")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Act(_clerk, id, "ombud-outcome", new { outcome = "DISMISSED" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Act(_clerk, id, "close")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var closed = await Get(_clerk, id);
        closed.GetProperty("status").GetString().ShouldBe("Closed");
        closed.GetProperty("reportable").GetBoolean().ShouldBeTrue("a rejected complaint is reportable (§6.2)");
        closed.GetProperty("retainUntil").GetString().ShouldBe(TestClock.Today.AddYears(5).ToString("yyyy-MM-dd"), "5 years when no retention is in force (OQ-106)");
        closed.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("eventType").GetString()).ShouldBe(
            ["RECEIVED", "LINKED", "ACKNOWLEDGED", "EXTENDED", "DECIDED", "REFERRED_TO_OMBUD", "OMBUD_OUTCOME", "CLOSED"]);
        var message = await Act(_clerk, id, "messages", new { body = "Anything else?" });
        (await message.Code()).ShouldBe("CASE_CLOSED");

        await _factory.DrainOutbox();
        foreach (var type in new[] { "workflow.CaseOpened", "workflow.CaseAcknowledged", "workflow.CaseUpdated", "workflow.CaseDecided", "workflow.CaseReferredToOmbud", "workflow.CaseClosed" })
        {
            (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.outbox_event WHERE aggregate_id = @id AND event_type = @type", ("id", id), ("type", type)))
                .ShouldBeGreaterThanOrEqualTo(1, type);
        }

        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.outbox_event WHERE aggregate_id = @id AND payload::text LIKE '%ignored%'", ("id", id)))
            .ShouldBe(0, "events carry identifiers, never the complaint's text (INT-007)");

        // The 18.8.3 report and the dashboard.
        var report = await (await _clerk.GetAsync($"{Cases}/complaints-report?from={TestClock.Today.AddDays(-1):yyyy-MM-dd}&to={TestClock.Today.AddDays(1):yyyy-MM-dd}")).Json();
        var category = report.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryCode").GetString() == "RPT");
        category.GetProperty("pprCategory").GetString().ShouldBe("SERVICE");
        category.GetProperty("received").GetInt64().ShouldBe(1);
        category.GetProperty("rejected").GetInt64().ShouldBe(1);
        category.GetProperty("referredToOmbud").GetInt64().ShouldBe(1);
        category.GetProperty("rejectionReasons").EnumerateArray().Single().GetString().ShouldBe("The loss is excluded under clause 4.");
        category.GetProperty("ombudOutcomes").EnumerateArray().Single().GetProperty("key").GetString().ShouldBe("DISMISSED");
        category.GetProperty("reportable").GetInt64().ShouldBe(1);
        var figures = (await (await _clerk.GetAsync($"{Cases}/summary")).Json()).GetProperty("figures").EnumerateArray()
            .ToDictionary(f => f.GetProperty("code").GetString()!, f => f);
        figures.Keys.ShouldBe(CaseFigures.All, ignoreOrder: true);
        figures[CaseFigures.ComplaintsRejected].GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        figures[CaseFigures.ComplaintsReferredToOmbud].GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        figures[CaseFigures.ComplaintsReceived].GetProperty("count").GetInt64().ShouldBeGreaterThanOrEqualTo(1);

        // Lists: by linked record, by status.
        var byLink = await (await _clerk.GetAsync($"{Cases}?linkType=POLICY&linkId={policy}")).Json();
        byLink.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([id]);
        var byStatus = await (await _clerk.GetAsync($"{Cases}?status=CLOSED&family=COMPLAINT")).Json();
        byStatus.GetProperty("items").EnumerateArray().ShouldContain(c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task AComplaintUpheldWithinTheQueryWindow_IsNotReportable_AndOneUpheldAfterItIs_PPR18_1()
    {
        var inside = await OpenComplaint();
        var outside = await OpenComplaint();
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.service_case SET received_at = now() - interval '20 days' WHERE id = @id", ("id", outside));
        var atReceipt = await Open(_clerk, new
        {
            caseTypeCode = "COMPLAINT_T", receivedChannel = "WALK_IN", complainantName = "A walk-in", complainantContact = "082 000 0000", categoryCode = "SERVICE",
            summary = "Fixed at the counter", upheldAtReceipt = true,
        });

        foreach (var id in new[] { inside, outside, atReceipt.GetProperty("id").GetGuid() })
        {
            (await Act(_clerk, id, "acknowledge")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await Act(_clerk, id, "decide", new { outcome = "UPHELD" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await Act(_clerk, id, "close")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await Get(_clerk, inside)).GetProperty("reportable").GetBoolean().ShouldBeFalse("upheld within the 5 business day query window");
        (await Get(_clerk, outside)).GetProperty("reportable").GetBoolean().ShouldBeTrue("upheld after the query window");
        (await Get(_clerk, atReceipt.GetProperty("id").GetGuid())).GetProperty("reportable").GetBoolean().ShouldBeFalse("upheld at receipt");
    }

    [Fact]
    public async Task APortalMember_RaisesAndSeesOnlyTheirOwnCases_AndAppealsADecision_Cases5()
    {
        await PortalChannels();
        var party = Guid.CreateVersion7();
        await PortalUser("member-1", "PARTY", party, null, null);
        await PortalUser("member-2", "PARTY", Guid.CreateVersion7(), null, null);
        var member = _factory.ClientFor(T, "member-1", "member-portal");
        var other = _factory.ClientFor(T, "member-2", "member-portal");

        var refused = await member.Send(HttpMethod.Post, Portal, new
        {
            caseTypeCode = "COMPLAINT_T", categoryCode = "SERVICE", summary = "Not my policy",
            links = new[] { new { linkType = "POLICY", recordId = Guid.CreateVersion7(), reference = "GP-X" } },
        }, idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("OUT_OF_SCOPE");
        (await (await member.Send(HttpMethod.Post, Portal, new { caseTypeCode = "DISPUTE_P", summary = "A provider type" }, idempotent: true)).Code())
            .ShouldBe("CASE_TYPE_NOT_IN_FORCE");

        var raised = await member.Send(HttpMethod.Post, Portal, new { caseTypeCode = "COMPLAINT_T", categoryCode = "SERVICE", summary = "Slow service" }, idempotent: true);
        raised.StatusCode.ShouldBe(HttpStatusCode.Created, await raised.Content.ReadAsStringAsync());
        var id = (await raised.Json()).GetProperty("id").GetGuid();
        (await member.Send(HttpMethod.Post, $"{Portal}/{id}/messages", new { body = "Any news?" }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await (await member.GetAsync(Portal)).Json()).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ShouldBe([id]);
        (await (await other.GetAsync(Portal)).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await other.GetAsync($"{Portal}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Send(HttpMethod.Post, $"{Portal}/{id}/messages", new { body = "Hi" }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var staffView = await Get(_clerk, id);
        (staffView.GetProperty("raisedByAudience").GetString(), staffView.GetProperty("complainantPartyId").GetGuid(), staffView.GetProperty("receivedChannel").GetString())
            .ShouldBe(("CUSTOMER", party, "PORTAL"));
        (await Act(_clerk, id, "acknowledge")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Act(_clerk, id, "decide", new { outcome = "REJECTED", reasons = "Within our service standards." })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var view = await (await member.GetAsync($"{Portal}/{id}")).Json();
        view.GetProperty("decisionReasons").GetString().ShouldBe("Within our service standards.");
        view.GetProperty("reviewOptions").GetString()!.ShouldContain("REVIEW_T");
        view.GetProperty("ombudDetails").GetString()!.ShouldContain("National Financial Ombud Scheme");
        view.GetProperty("messages").EnumerateArray().Single().GetProperty("direction").GetString().ShouldBe("IN");
        view.TryGetProperty("processInstanceId", out _).ShouldBeFalse("portal views leave out internal fields");

        var appealed = await member.Send(HttpMethod.Post, $"{Portal}/{id}/appeal", new { summary = "Please review" }, idempotent: true);
        appealed.StatusCode.ShouldBe(HttpStatusCode.Created, await appealed.Content.ReadAsStringAsync());
        var appeal = await appealed.Json();
        (appeal.GetProperty("family").GetString(), appeal.GetProperty("parentCaseId").GetGuid()).ShouldBe(("APPEAL", id));
    }

    [Fact]
    public async Task ABroker_SeesTheCasesItRaised_AndACaseOnItsPolicyOnlyOnceShared_OQ108()
    {
        await PortalChannels();
        var policy = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO general.policy (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, policy_number, product_code, product_version,
                                        rating_version, policyholder_party_id, status, period_start, period_end, currency, annual_premium, billing_account_ref,
                                        cover_starts_on, intermediary_code)
            VALUES (@t, @p, 1, now(), 'seed', now(), 'seed', @n, 'MOTOR', 1, 1, gen_random_uuid(), 'IN_FORCE', current_date - 10, current_date + 355, 'ZAR', 1200,
                    gen_random_uuid(), 'ISSUE', 'BRK-CASE-A')
            """, ("t", T), ("p", policy), ("n", "GP-" + policy.ToString("N")[..12]));
        await PortalUser("broker-case-a", "INTERMEDIARY", null, "BRK-CASE-A", null);
        await PortalUser("broker-case-b", "INTERMEDIARY", null, "BRK-CASE-B", null);
        var brokerA = _factory.ClientFor(T, "broker-case-a", "broker-portal", multiFactor: true);
        var brokerB = _factory.ClientFor(T, "broker-case-b", "broker-portal", multiFactor: true);

        var raised = await brokerA.Send(HttpMethod.Post, Portal, new { caseTypeCode = "ENQUIRY_T", summary = "Commission query" }, idempotent: true);
        raised.StatusCode.ShouldBe(HttpStatusCode.Created, await raised.Content.ReadAsStringAsync());
        var own = (await raised.Json()).GetProperty("id").GetGuid();
        var client = Guid.CreateVersion7();
        var forClient = await brokerA.Send(HttpMethod.Post, Portal, new
        {
            caseTypeCode = "COMPLAINT_T", categoryCode = "SERVICE", summary = "For my client", onBehalfOfPartyId = client,
            links = new[] { new { linkType = "POLICY", recordId = policy, reference = "GP-1" } },
        }, idempotent: true);
        forClient.StatusCode.ShouldBe(HttpStatusCode.Created, await forClient.Content.ReadAsStringAsync());
        (await brokerB.Send(HttpMethod.Post, Portal, new
        {
            caseTypeCode = "COMPLAINT_T", categoryCode = "SERVICE", summary = "Not mine", onBehalfOfPartyId = client,
            links = new[] { new { linkType = "POLICY", recordId = policy, reference = "GP-1" } },
        }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A case staff opened on the broker's policy: not seen until shared.
        var staffCase = (await Open(_clerk, new
        {
            caseTypeCode = "COMPLAINT_T", receivedChannel = "PHONE", complainantPartyId = Guid.CreateVersion7(), categoryCode = "SERVICE", summary = "Policyholder called",
            links = new[] { new { linkType = "POLICY", recordId = policy, reference = "GP-1" } },
        })).GetProperty("id").GetGuid();
        (await brokerA.GetAsync($"{Portal}/{staffCase}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Act(_clerk, staffCase, "share-with-broker")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await brokerA.GetAsync($"{Portal}/{staffCase}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await brokerB.GetAsync($"{Portal}/{staffCase}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var seen = (await (await brokerA.GetAsync(Portal)).Json()).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        seen.ShouldContain(own);
        seen.ShouldContain(staffCase);
        (await (await brokerB.GetAsync(Portal)).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task AProvider_SeesOnlyItsOwnDisputes_AndStaffReadThemOnlyWithHealthClaimRead_Cases9()
    {
        await PortalChannels();
        await PortalUser("provider-case-1", "PROVIDER", null, null, "PR-CASE-1");
        await PortalUser("provider-case-2", "PROVIDER", null, null, "PR-CASE-2");
        var provider = _factory.ClientFor(T, "provider-case-1", "provider-portal", multiFactor: true);
        var other = _factory.ClientFor(T, "provider-case-2", "provider-portal", multiFactor: true);

        (await (await provider.Send(HttpMethod.Post, Portal, new { caseTypeCode = "ENQUIRY_T", summary = "Not for providers" }, idempotent: true)).Code())
            .ShouldBe("CASE_TYPE_NOT_IN_FORCE");
        var raised = await provider.Send(HttpMethod.Post, Portal, new { caseTypeCode = "DISPUTE_P", summary = "Short payment on claim 123" }, idempotent: true);
        raised.StatusCode.ShouldBe(HttpStatusCode.Created, await raised.Content.ReadAsStringAsync());
        var id = (await raised.Json()).GetProperty("id").GetGuid();

        (await provider.GetAsync($"{Portal}/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync($"{Portal}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _clerk.GetAsync($"{Cases}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a Health case needs medical_claim.read");
        (await (await _clerk.GetAsync($"{Cases}?type=DISPUTE_P")).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);
        var health = _factory.ClientFor(T, "case-health");
        (await health.GetAsync($"{Cases}/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Get(health, id)).GetProperty("raisedByProviderId").GetString().ShouldBe("PR-CASE-1");
    }

    [Fact]
    public async Task Redress_IsApprovedByAnotherPerson_ThenPaid_AndARejectedOneIsRejected_OQ104()
    {
        var id = await OpenComplaint();
        var payee = Guid.CreateVersion7();
        var recorded = await _clerk.Send(HttpMethod.Post, $"{Cases}/{id}/redress", new { redressType = "COMPENSATION", amount = 750.50m, currency = "ZAR", payeePartyId = payee },
            idempotent: true);
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created, await recorded.Content.ReadAsStringAsync());
        var requested = await recorded.Json();
        var redress = requested.GetProperty("id").GetGuid();
        requested.GetProperty("status").GetString().ShouldBe("Requested");
        await _factory.DrainOutbox();

        var approval = await Approval(redress);
        var byMaker = await Decide(_clerk, approval, "approve");
        (await byMaker.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await Decide(_reviewer, approval, "approve")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();

        var approved = await (await _clerk.GetAsync($"{Cases}/{id}/redress/{redress}")).Json();
        approved.GetProperty("status").GetString().ShouldBe("Approved");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.outbox_event WHERE event_type = 'workflow.CaseRedressApproved' AND aggregate_id = @r",
            ("r", redress))).ShouldBe(1);

        var payment = Guid.CreateVersion7();
        var paid = new Payments.Contracts.PaymentSucceeded(payment, "PAY-1", CaseApprovals.RedressPaymentPurpose, 750.50m, "ZAR", TestClock.Today,
            CaseApprovals.RedressBusinessRefType, redress);
        await _factory.Services.GetRequiredService<IEventBus>().Deliver(new EventEnvelope(Guid.CreateVersion7(), T, "payment.PaymentSucceeded", 1, DateTimeOffset.UtcNow,
            "Payment", payment, Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(paid, JsonSerializerOptions.Web)), CancellationToken.None);
        var settled = await (await _clerk.GetAsync($"{Cases}/{id}/redress/{redress}")).Json();
        (settled.GetProperty("status").GetString(), settled.GetProperty("paymentId").GetGuid()).ShouldBe(("Paid", payment));

        var second = await (await _clerk.Send(HttpMethod.Post, $"{Cases}/{id}/redress", new { redressType = "GOODWILL", amount = 100m, currency = "ZAR", payeePartyId = payee },
            idempotent: true)).Json();
        await _factory.DrainOutbox();
        var secondApproval = await Approval(second.GetProperty("id").GetGuid());
        (await _reviewer.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{secondApproval}/reject", new { reason = "Not warranted" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DrainOutbox();
        var rejected = await (await _clerk.GetAsync($"{Cases}/{id}/redress/{second.GetProperty("id").GetGuid()}")).Json();
        rejected.GetProperty("status").GetString().ShouldBe("Rejected");
        rejected.GetProperty("latestRejection").GetProperty("reason").GetString().ShouldBe("Not warranted");

        var figures = (await (await _clerk.GetAsync($"{Cases}/summary")).Json()).GetProperty("figures").EnumerateArray()
            .Single(f => f.GetProperty("code").GetString() == CaseFigures.RedressPaid);
        figures.GetProperty("amounts").EnumerateArray().Single(a => a.GetProperty("currency").GetString() == "ZAR").GetProperty("amount").GetDecimal()
            .ShouldBeGreaterThanOrEqualTo(750.50m);
    }

    [Fact]
    public async Task ALegalHold_KeepsAClosedCasePastItsRetention_AndTheOtherIsDisposedOf_OQ106()
    {
        var held = await ClosedComplaint();
        var disposed = await ClosedComplaint();
        (await Act(_clerk, held, "legal-hold", new { reason = "Litigation" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.service_case SET retain_until = current_date - 30 WHERE id = ANY(@ids)", ("ids", new[] { held, disposed }));

        var (_, removed) = await _factory.Services.GetRequiredService<CaseTimelineJobs>().RunForTenant(T, CancellationToken.None);
        removed.ShouldBeGreaterThanOrEqualTo(1);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.service_case WHERE id = @id", ("id", held))).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.service_case WHERE id = @id", ("id", disposed))).ShouldBe(0);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.case_event WHERE case_id = @id", ("id", disposed))).ShouldBe(0);

        (await Act(_clerk, held, "legal-hold/release", new { reason = "Settled" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.Services.GetRequiredService<CaseTimelineJobs>().RunForTenant(T, CancellationToken.None);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.service_case WHERE id = @id", ("id", held))).ShouldBe(0);
    }

    /// <summary>A legal hold on the case itself (D-34, document.record_hold) keeps it past its retention too, until the hold is released.</summary>
    [Fact]
    public async Task ARecordHoldOnTheCase_KeepsItPastItsRetention_UntilReleased_D34()
    {
        var held = await ClosedComplaint();
        var hold = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsOwner("""
            UPDATE workflow.service_case SET retain_until = current_date - 30 WHERE id = @c;
            INSERT INTO document.record_hold (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, scope_type, scope_id, reason_code, placed_by, placed_at)
            VALUES (@t, @h, 1, now(), 'test', now(), 'test', 'CASE', @c, 'LITIGATION', gen_random_uuid(), now());
            """, ("t", T), ("c", held), ("h", hold));

        await _factory.Services.GetRequiredService<CaseTimelineJobs>().RunForTenant(T, CancellationToken.None);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.service_case WHERE id = @id", ("id", held))).ShouldBe(1, "the case is held");

        await fixture.Database.ExecuteAsOwner("UPDATE document.record_hold SET released_at = now() WHERE id = @h", ("h", hold));
        await _factory.Services.GetRequiredService<CaseTimelineJobs>().RunForTenant(T, CancellationToken.None);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.service_case WHERE id = @id", ("id", held))).ShouldBe(0, "the hold was released");
    }

    [Fact]
    public async Task AnOverdueStep_IsToldToTheComplainantOnce_Cases7()
    {
        var id = await OpenComplaint();
        await fixture.Database.ExecuteAsOwner("UPDATE workflow.service_case SET expected_by = now() - interval '1 hour' WHERE id = @id", ("id", id));
        var jobs = _factory.Services.GetRequiredService<CaseTimelineJobs>();
        (await jobs.RunForTenant(T, CancellationToken.None)).Overdue.ShouldBeGreaterThanOrEqualTo(1);
        await jobs.RunForTenant(T, CancellationToken.None);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.outbox_event WHERE aggregate_id = @id AND event_type = 'workflow.CaseUpdated'", ("id", id)))
            .ShouldBe(1);
        (await Get(_clerk, id)).GetProperty("overdue").GetBoolean().ShouldBeTrue();
        (await (await _clerk.GetAsync($"{Cases}?overdue=true")).Json()).GetProperty("items").EnumerateArray().ShouldContain(c => c.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task ACaseRaisedByAPerson_OpensWithoutTimelines_AndALinkTheTypeDoesNotAllowIsRefused_Cases3()
    {
        var enquiry = await Open(_clerk, new { caseTypeCode = "ENQUIRY_T", receivedChannel = "PHONE", complainantName = "Caller", summary = "Where is my policy?" });
        enquiry.GetProperty("expectedBy").ValueKind.ShouldBe(JsonValueKind.Null);
        var id = enquiry.GetProperty("id").GetGuid();
        (await (await Act(_clerk, id, "links", new { linkType = "INVOICE", recordId = Guid.CreateVersion7(), reference = "INV-1" })).Code()).ShouldBe("CASE_LINK_NOT_ALLOWED");
        (await Act(_clerk, id, "close")).StatusCode.ShouldBe(HttpStatusCode.NoContent, "an enquiry is answered straight from RECEIVED");
        (await Get(_clerk, id)).GetProperty("outcome").GetString().ShouldBe("ANSWERED");
        (await (await _clerk.Send(HttpMethod.Post, Cases, new { caseTypeCode = "COMPLAINT_T", receivedChannel = "PHONE", complainantName = "X", summary = "No category" },
            idempotent: true)).Code()).ShouldBe("CASE_CATEGORY_REQUIRED");
    }

    private async Task<Guid> OpenComplaint() =>
        (await Open(_clerk, new { caseTypeCode = "COMPLAINT_T", receivedChannel = "PHONE", complainantPartyId = Guid.CreateVersion7(), categoryCode = "SERVICE", summary = "S" }))
        .GetProperty("id").GetGuid();

    private async Task<Guid> ClosedComplaint()
    {
        var id = await OpenComplaint();
        (await Act(_clerk, id, "acknowledge")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Act(_clerk, id, "decide", new { outcome = "REJECTED", reasons = "R" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Act(_clerk, id, "close")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    private static async Task<JsonElement> Open(HttpClient client, object body)
    {
        var response = await client.Send(HttpMethod.Post, Cases, body, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static async Task<JsonElement> Get(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{Cases}/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static async Task<HttpResponseMessage> Act(HttpClient client, Guid id, string action, object? body = null)
    {
        var read = await client.GetAsync($"{Cases}/{id}");
        var etag = read.StatusCode == HttpStatusCode.OK ? read.ETag() : "\"1\"";
        return await client.Send(HttpMethod.Post, $"{Cases}/{id}/{action}", body ?? new { }, ifMatch: etag);
    }

    private Task<Guid> Approval(Guid redress) =>
        fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE business_ref_type = 'CASE_REDRESS' AND business_ref_id = @r AND status = 'REQUESTED'",
            ("r", redress));

    private static async Task<HttpResponseMessage> Decide(HttpClient client, Guid approval, string action)
    {
        var version = (await (await client.GetAsync($"/api/v1/workflow/approvals/{approval}")).Json()).GetProperty("rowVersion").GetInt64();
        return await client.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/{action}", ifMatch: $"\"{version}\"");
    }

    private async Task PortalChannels()
    {
        foreach (var (code, audience, clientId) in new[] { ("MEMC", "CUSTOMER", "member-portal"), ("BRKC", "BROKER", "broker-portal"), ("PRVC", "PROVIDER", "provider-portal") })
        {
            await fixture.Database.SeedStatic(T, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-" + code, From, ("channel_code", code), ("audience", audience),
                ("name", audience + " portal"), ("client_ids", new[] { clientId }));
        }
    }

    private async Task PortalUser(string subject, string kind, Guid? party, string? broker, string? provider)
    {
        var user = await fixture.Database.UserWithPermissions(T, subject, CasePermissions.Read, CasePermissions.Create);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            SELECT @t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @i, @pr, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now()
            WHERE NOT EXISTS (SELECT 1 FROM iam.user_subject_link WHERE tenant_id = @t AND user_id = @u)
            """, ("t", T), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("i", (object?)broker ?? DBNull.Value), ("pr", (object?)provider ?? DBNull.Value));
    }
}
