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
using Insys.General.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// DE-039 part 2 (Ade, 25 November 2026; OQ-201): a broker records its client's acknowledgement of a disclosure, for its own clients only (BR-091), saying how the
/// notice was presented; it is stored with the text version, language, channel, broker and recording user, and audited. The customer sees it in the member portal.
/// A tenant can switch broker recording off (customer-only): the action is refused with DISCLOSURE_BROKER_NOT_ALLOWED and the notice still reads.
/// </summary>
public sealed class BrokerDisclosureTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid A = DatabaseFixture.TenantA;
    private static readonly Guid B = DatabaseFixture.TenantB;
    private static readonly DateOnly From = new(2026, 1, 1);
    private const string Text = "/api/v1/portal/broker/disclosures?displayPoint=ISSUE&noticeType=POLICY_TERMS&jurisdiction=ZA&language=en";
    private const string Acknowledge = "/api/v1/portal/broker/disclosure-acknowledgements";

    private InsysApiFactory _factory = null!;
    private RowFiller _rows = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        // The new BROKER_PORTAL and staff functions, from the code's declarations: the shared R__function_catalogue.sql is regenerated at merge, not here.
        await fixture.Database.ExecuteAsOwner(FunctionCatalogueSeed.Generate());
        _rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        foreach (var tenant in new[] { A, B })
        {
            foreach (var (code, audience, clientId) in new[] { ("MEM39", "CUSTOMER", "member-portal"), ("BRK39", "BROKER", "broker-portal") })
            {
                await fixture.Database.SeedStatic(tenant, "channel.cfg_channel_definitions", "EFFECTIVE", "CH-" + code, From, ("channel_code", code), ("audience", audience),
                    ("name", audience + " portal"), ("client_ids", new[] { clientId }));
            }

            await fixture.Database.SeedStatic(tenant, "channel.cfg_disclosure_and_notice_texts", "EFFECTIVE", "DISC-39", From, ("notice_type", "POLICY_TERMS"), ("jurisdiction", "ZA"),
                ("language", "en"), ("text", "Your cover starts when issued."), ("text_version", 3), ("display_point", "ISSUE"));
            foreach (var (code, node) in new[] { ("P39", "N39-P"), ("O39", "N39-O") })
            {
                await fixture.Database.SeedStatic(tenant, "commission.cfg_distribution_hierarchy", "EFFECTIVE", node, From, ("node_code", node), ("parent", DBNull.Value),
                    ("overriding_commission", false));
                await fixture.Database.SeedStatic(tenant, "commission.mst_intermediary_register", "ACTIVE", code, From, ("intermediary_code", code), ("type", "BROKER"),
                    ("legal_name", code), ("licence_numbers", JsonSerializer.SerializeToElement(new[] { "FSP-39" })), ("status", "ACTIVE"), ("hierarchy_node", node));
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ABroker_RecordsItsOwnClientsAcknowledgement_WithEveryField_Audited_AndTheMemberSeesIt_DE039_BR091()
    {
        var client = await Party(A, "Broker's client");
        var quote = await _rows.Insert(A, "general.quote", ("status", "DRAFT"), ("proposer_party_id", client), ("intermediary_code", "P39"));
        var broker = await Broker(A, "broker-39p", "P39");

        var text = await (await broker.GetAsync(Text)).Json();
        (text.GetProperty("textVersion").GetInt32(), text.GetProperty("brokerAcknowledgementAllowed").GetBoolean()).ShouldBe((3, true));

        var recorded = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "OTHER", "Read aloud at the client's office with an interpreter", quote), idempotent: true);
        recorded.StatusCode.ShouldBe(HttpStatusCode.Created, await recorded.Content.ReadAsStringAsync());
        var view = await recorded.Json();
        var id = view.GetProperty("id").GetGuid();
        (view.GetProperty("partyId").GetGuid(), view.GetProperty("textVersion").GetInt32()).ShouldBe((client, 3));
        view.GetProperty("outstanding").GetArrayLength().ShouldBe(0, "the issue's only notice is acknowledged");

        var stored = await fixture.Database.ScalarAsOwner<string>("""
            SELECT concat_ws('|', party_id, notice_type, display_point, text_version, language, jurisdiction, business_ref_type, business_ref_id, channel_code, intermediary_code,
              presentation_method, presentation_detail, recorded_by IS NOT NULL AND recorded_by <> '')
            FROM channel.disclosure_acknowledgement WHERE tenant_id = @t AND id = @id
            """, ("t", A), ("id", id));
        stored.ShouldBe($"{client}|POLICY_TERMS|ISSUE|3|en|ZA|{GeneralDocuments.QuoteRefType}|{quote}|BRK39|P39|OTHER|Read aloud at the client's office with an interpreter|t", "the broker's channel is the channel of its portal client");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT concat_ws('|', details ->> 'broker', details ->> 'method', details ->> 'version', details ->> 'language') FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'BrokerAcknowledgeDisclosure' AND outcome = 'SUCCESS' AND target_id = @id
            """, ("t", A), ("id", id.ToString()))).ShouldBe("P39|OTHER|3|en");

        // The customer sees it in the member portal: recorded by their broker, with the method and the date.
        var member = await Member(A, "member-39", client);
        var seen = (await (await member.GetAsync("/api/v1/portal/disclosure-acknowledgements")).Json()).EnumerateArray().Single();
        (seen.GetProperty("id").GetGuid(), seen.GetProperty("recordedBy").GetString(), seen.GetProperty("intermediaryCode").GetString(), seen.GetProperty("presentationMethod").GetString())
            .ShouldBe((id, "BROKER", "P39", "OTHER"));
        // The date, as stored (to the microsecond).
        (seen.GetProperty("acknowledgedAt").GetDateTimeOffset() - view.GetProperty("acknowledgedAt").GetDateTimeOffset()).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
        (await (await (await Member(A, "member-39-other", await Party(A, "Someone else"))).GetAsync("/api/v1/portal/disclosure-acknowledgements")).Json())
            .GetArrayLength().ShouldBe(0, "another member sees none of it");
    }

    [Fact]
    public async Task ABroker_IsRefusedAnotherBrokersClient_AsNotFound_BR091_DE039()
    {
        var others = await Party(A, "Another broker's client");
        await _rows.Insert(A, "general.policy", ("policyholder_party_id", others), ("intermediary_code", "O39"), ("currency", "ZAR"));
        var broker = await Broker(A, "broker-39p-2", "P39");

        var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(others, "IN_PERSON"), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, await refused.Content.ReadAsStringAsync());
        (await Count(A, others)).ShouldBe(0);

        // Its own broker may.
        var own = await Broker(A, "broker-39o", "O39");
        (await own.Send(HttpMethod.Post, Acknowledge, Body(others, "PHONE"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task APresentationMethod_IsRequired_AndOtherNeedsADetailOfAtMost200Characters_DE039()
    {
        var client = await Party(A, "Client for validation");
        await _rows.Insert(A, "general.policy", ("policyholder_party_id", client), ("intermediary_code", "P39"), ("currency", "ZAR"));
        var broker = await Broker(A, "broker-39p-3", "P39");

        foreach (var (method, detail) in new (string?, string?)[] { (null, null), ("POST", null), ("OTHER", null), ("OTHER", "  "), ("OTHER", new string('x', 201)) })
        {
            var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, method, detail), idempotent: true);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{method}/{detail?.Length}");
            (await refused.Code()).ShouldBe("VALIDATION_FAILED");
        }

        (await Count(A, client)).ShouldBe(0);
        (await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "EMAIL"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// OQ-201 (Ade, 26 November 2026): the record a broker names must exist and belong to that client. Another party's record is not found, as one that does not
    /// exist (BR-091); a reference type no module can check is refused; the client's own quote, renewal or proposal is accepted.
    /// </summary>
    [Fact]
    public async Task TheRecordABrokerNames_MustExistAndBelongToTheClient_AnotherPartysIsNotFound_OQ201_BR091()
    {
        var client = await Party(A, "Client with records");
        var other = await Party(A, "Another party of the same broker");
        var broker = await Broker(A, "broker-39p-5", "P39");
        await _rows.Insert(A, "general.quote", ("status", "DRAFT"), ("proposer_party_id", client), ("intermediary_code", "P39"));
        var othersQuote = await _rows.Insert(A, "general.quote", ("status", "DRAFT"), ("proposer_party_id", other), ("intermediary_code", "P39"));
        var othersPolicy = await _rows.Insert(A, "general.policy", ("policyholder_party_id", other), ("intermediary_code", "P39"), ("currency", "ZAR"));
        var othersRenewal = await _rows.Insert(A, "general.renewal", ("policy_id", othersPolicy), ("status", "REVIEWING"));
        var ownPolicy = await _rows.Insert(A, "general.policy", ("policyholder_party_id", client), ("intermediary_code", "P39"), ("currency", "ZAR"));
        var ownRenewal = await _rows.Insert(A, "general.renewal", ("policy_id", ownPolicy), ("status", "REVIEWING"));
        var ownProposal = await _rows.Insert(A, "life.proposal", ("applicant_party_id", client), ("intermediary_code", "P39"));

        foreach (var (type, id, why) in new[]
                 {
                     (GeneralDocuments.QuoteRefType, othersQuote, "another party's quote"), (GeneralDocuments.RenewalRefType, othersRenewal, "another party's renewal"),
                     (GeneralDocuments.QuoteRefType, Guid.CreateVersion7(), "a quote that doesn't exist"), ("LIFE_PROPOSAL", Guid.CreateVersion7(), "a proposal that doesn't exist"),
                 })
        {
            var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "IN_PERSON", reference: (type, id)), idempotent: true);
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, why);
            (await refused.Code()).ShouldBe("NOT_FOUND", why);
        }

        foreach (var (type, id, why) in new (string?, Guid?, string)[]
                 {
                     (GeneralDocuments.PolicyRefType, ownPolicy, "a type no module checks for the client"), (GeneralDocuments.QuoteRefType, null, "a type without its record"),
                     (null, ownPolicy, "a record without its type"),
                 })
        {
            var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "IN_PERSON", reference: (type, id)), idempotent: true);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, why);
            (await refused.Code()).ShouldBe("VALIDATION_FAILED", why);
        }

        (await Count(A, client)).ShouldBe(0, "nothing was recorded");
        foreach (var (type, id) in new[] { (GeneralDocuments.RenewalRefType, ownRenewal), ("LIFE_PROPOSAL", ownProposal) })
        {
            var accepted = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "PHONE", reference: (type, id)), idempotent: true);
            accepted.StatusCode.ShouldBe(HttpStatusCode.Created, $"{type}: {await accepted.Content.ReadAsStringAsync()}");
        }

        (await Count(A, client)).ShouldBe(2, "the client's own renewal and proposal");
    }

    /// <summary>
    /// OQ-201 (Ade, 26 November 2026): switching broker acknowledgement off goes through a second person. The PUT answers 202 with the pending change; the value
    /// is unchanged until the grant; a second request waits; the requester can't approve it. Once granted, the broker is refused and the notice still reads.
    /// Tenant B is switched, so tenant A's other tests are untouched.
    /// </summary>
    [Fact]
    public async Task SwitchingToCustomerOnly_WaitsForASecondPerson_NotTheRequester_AndOnceGrantedTheBrokerIsRefused_OQ201_SRS002()
    {
        var client = await Party(B, "Customer-only client");
        await _rows.Insert(B, "general.policy", ("policyholder_party_id", client), ("intermediary_code", "P39"), ("currency", "ZAR"));
        var broker = await Broker(B, "broker-39p-b", "P39");
        var people = await SettingPeople(B, "b");

        var setting = await people.Requester.GetAsync("/api/v1/integration/disclosure-setting");
        var current = await setting.Json();
        (current.GetProperty("brokerAcknowledgementAllowed").GetBoolean(), current.GetProperty("isDefault").GetBoolean()).ShouldBe((true, true));
        current.GetProperty("pendingChange").ValueKind.ShouldBe(JsonValueKind.Null);
        (await people.Requester.Send(HttpMethod.Put, SettingPath, new { })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the switch is named");

        var asked = await people.Requester.Send(HttpMethod.Put, SettingPath, new { brokerAcknowledgementAllowed = false });
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync());
        var pending = await asked.Json();
        pending.GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeTrue("unchanged until the grant");
        (pending.GetProperty("pendingChange").GetProperty("brokerAcknowledgementAllowed").GetBoolean(), pending.GetProperty("pendingChange").GetProperty("requestedBy").GetGuid())
            .ShouldBe((false, people.RequesterId));
        asked.ETag().ShouldBe($"\"{pending.GetProperty("rowVersion").GetInt64()}\"");

        // Until the grant: the GET shows the pending change, the broker still records, and a second request is refused.
        var read = await people.Requester.GetAsync(SettingPath);
        (await read.Json()).GetProperty("pendingChange").GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeFalse();
        (await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "IN_PERSON"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created, "the value is unchanged");
        var second = await people.Requester.Send(HttpMethod.Put, SettingPath, new { brokerAcknowledgementAllowed = false }, ifMatch: read.ETag());
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Code()).ShouldBe("INVALID_STATE_TRANSITION");
        (await people.Requester.Send(HttpMethod.Put, SettingPath, new { brokerAcknowledgementAllowed = true })).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed, "once the tenant has its own setting, a change needs If-Match");

        var approval = await Approval(B, people.RequesterId);
        (await Decide(people.Requester, approval, "approve")).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        await _factory.DrainOutbox();
        (await (await people.Requester.GetAsync(SettingPath)).Json()).GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeTrue("still unchanged");
        (await Decide(people.Checker, approval, "approve")).ShouldBeNull();
        await _factory.DrainOutbox();

        var after = await (await people.Requester.GetAsync(SettingPath)).Json();
        (after.GetProperty("brokerAcknowledgementAllowed").GetBoolean(), after.GetProperty("isDefault").GetBoolean()).ShouldBe((false, false));
        after.GetProperty("pendingChange").ValueKind.ShouldBe(JsonValueKind.Null);
        (await AuditDetails(B, "RequestChangeDisclosureSetting")).ShouldBe("false|");
        (await AuditDetails(B, "ChangeDisclosureSetting")).ShouldBe("false|GRANTED");

        var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "PHONE"), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("DISCLOSURE_BROKER_NOT_ALLOWED");
        (await Count(B, client)).ShouldBe(1, "only the one recorded before the grant");
        var text = await broker.GetAsync(Text);
        text.StatusCode.ShouldBe(HttpStatusCode.OK, "the screen still shows the notice");
        (await text.Json()).GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeFalse();

        // Tenant A, untouched, still allows it.
        var clientA = await Party(A, "Allowed client");
        await _rows.Insert(A, "general.policy", ("policyholder_party_id", clientA), ("intermediary_code", "P39"), ("currency", "ZAR"));
        (await (await Broker(A, "broker-39p-4", "P39")).Send(HttpMethod.Post, Acknowledge, Body(clientA, "IN_PERSON"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>OQ-201 (Ade, 26 November 2026): rejected, the setting stays as it was and the pending change is cleared, audited; a new request can then be made.</summary>
    [Fact]
    public async Task ARejectedChange_LeavesTheSettingAsItWas_AndClearsThePendingChange_OQ201()
    {
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "DS" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        var people = await SettingPeople(tenant, "r");

        (await people.Requester.Send(HttpMethod.Put, SettingPath, new { brokerAcknowledgementAllowed = false })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var approval = await Approval(tenant, people.RequesterId);
        (await Decide(people.Checker, approval, "reject")).ShouldBeNull();
        await _factory.DrainOutbox();

        var read = await people.Requester.GetAsync(SettingPath);
        var after = await read.Json();
        after.GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeTrue("as it was");
        after.GetProperty("pendingChange").ValueKind.ShouldBe(JsonValueKind.Null);
        (await AuditDetails(tenant, "ChangeDisclosureSettingNotApproved")).ShouldBe("false|REJECTED");
        (await AuditDetails(tenant, "ChangeDisclosureSetting")).ShouldBeNull("nothing changed");
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM integration.parked_delivery WHERE tenant_id = @t AND prerequisite_code IN ('APPROVAL_ENDING_NOT_APPLIED', 'APPROVAL_NOT_APPLIED')", ("t", tenant)))
            .ShouldBe(0L);

        (await people.Requester.Send(HttpMethod.Put, SettingPath, new { brokerAcknowledgementAllowed = false }, ifMatch: read.ETag())).StatusCode
            .ShouldBe(HttpStatusCode.Accepted, "a new request, once the first ended");
    }

    private const string SettingPath = "/api/v1/integration/disclosure-setting";

    private sealed record Approvers(HttpClient Requester, Guid RequesterId, HttpClient Checker);

    /// <summary>A requester and a checker, both with authority for DISCLOSURE_SETTING_CHANGE, so only maker-checker refuses the requester.</summary>
    private async Task<Approvers> SettingPeople(Guid tenant, string suffix)
    {
        string[] permissions =
            [IntegrationPermissions.DisclosureRead, IntegrationPermissions.DisclosureSettingUpdate, Workflow.Contracts.WorkflowPermissions.ApprovalRead, Workflow.Contracts.WorkflowPermissions.ApprovalApprove];
        var requester = await fixture.Database.UserWithPermissions(tenant, $"disclosure-requester-{suffix}", permissions);
        var checker = await fixture.Database.UserWithPermissions(tenant, $"disclosure-checker-{suffix}", permissions);
        var approver = await fixture.Database.RoleWithPermissions(tenant);
        await fixture.Database.AssignRole(tenant, requester, approver);
        await fixture.Database.AssignRole(tenant, checker, approver);
        await fixture.Database.SeedStatic(tenant, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-DSC-{suffix}", From,
            ("role_code", approver), ("transaction_type", "DISCLOSURE_SETTING_CHANGE"), ("limit_value", 0m), ("delegation_allowed", false));
        return new Approvers(_factory.ClientFor(tenant, $"disclosure-requester-{suffix}"), requester, _factory.ClientFor(tenant, $"disclosure-checker-{suffix}"));
    }

    /// <summary>Workflow's DISCLOSURE_SETTING_CHANGE approval of the tenant's setting, once the request is relayed, with the requester as its maker.</summary>
    private async Task<Guid> Approval(Guid tenant, Guid requester)
    {
        await _factory.DrainOutbox();
        var approval = await fixture.Database.ScalarAsOwner<Guid?>("""
            SELECT a.id FROM workflow.approval a JOIN channel.disclosure_setting s ON s.tenant_id = a.tenant_id AND s.id = a.business_ref_id
            WHERE a.tenant_id = @t AND a.business_ref_type = 'DISCLOSURE_SETTING' AND a.approval_type = 'DISCLOSURE_SETTING_CHANGE' AND a.status = 'REQUESTED'
            """, ("t", tenant));
        approval.ShouldNotBeNull("no DISCLOSURE_SETTING_CHANGE approval waits for the setting");
        (await fixture.Database.ScalarAsOwner<Guid>("SELECT maker_id FROM workflow.approval WHERE id = @a", ("a", approval.Value))).ShouldBe(requester);
        return approval.Value;
    }

    /// <summary>Decides the approval; null when it was accepted, else the refusal's code.</summary>
    private static async Task<string?> Decide(HttpClient client, Guid approval, string decision)
    {
        var read = await client.GetAsync($"/api/v1/workflow/approvals/{approval}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        var ifMatch = $"\"{(await read.Json()).GetProperty("rowVersion").GetInt64()}\"";
        var response = await client.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/{decision}", decision == "reject" ? new { reason = "Keep brokers recording" } : null,
            ifMatch: ifMatch);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Code() ?? response.StatusCode.ToString();
    }

    /// <summary>The tenant's audited operation's value and outcome, oldest first, once the outbox is drained.</summary>
    private async Task<string?> AuditDetails(Guid tenant, string operation)
    {
        await _factory.DrainOutbox();
        return await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(concat_ws('|', details ->> 'brokerAcknowledgementAllowed', coalesce(details ->> 'outcome', '')), ',' ORDER BY occurred_at)
            FROM audit.audit_event WHERE tenant_id = @t AND operation = @o AND outcome = 'SUCCESS'
            """, ("t", tenant), ("o", operation));
    }

    private static object Body(Guid partyId, string? presentationMethod, string? presentationDetail = null, Guid? quote = null, (string? Type, Guid? Id)? reference = null) => new
    {
        partyId, displayPoint = "ISSUE", noticeType = "POLICY_TERMS", jurisdiction = "ZA", language = "en",
        businessRefType = reference is { } r ? r.Type : quote is null ? null : GeneralDocuments.QuoteRefType, businessRefId = reference is { } r2 ? r2.Id : quote, presentationMethod, presentationDetail,
    };

    private Task<long> Count(Guid tenant, Guid party) =>
        fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM channel.disclosure_acknowledgement WHERE tenant_id = @t AND party_id = @p", ("t", tenant), ("p", party));

    private Task<Guid> Party(Guid tenant, string name) =>
        _rows.Insert(tenant, "party.party", ("party_kind", "INDIVIDUAL"), ("status_code", "ACTIVE"), ("display_name", name));

    private async Task<HttpClient> Member(Guid tenant, string subject, Guid party)
    {
        await Link(tenant, subject, "PARTY", party, null);
        return _factory.ClientFor(tenant, subject, "member-portal");
    }

    private async Task<HttpClient> Broker(Guid tenant, string subject, string code)
    {
        await Link(tenant, subject, "INTERMEDIARY", null, code);
        return _factory.ClientFor(tenant, subject, "broker-portal", multiFactor: true);
    }

    private async Task Link(Guid tenant, string subject, string kind, Guid? party, string? broker)
    {
        var user = await fixture.Database.UserWithPermissions(tenant, subject, IntegrationPermissions.DisclosureRead, IntegrationPermissions.DisclosureCreate, PartyPermissions.PartyRead);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id, intermediary_code,
                                               provider_id, status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, @k, @p, @i, NULL, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", tenant), ("u", user), ("k", kind), ("p", (object?)party ?? DBNull.Value), ("i", (object?)broker ?? DBNull.Value));
    }
}
