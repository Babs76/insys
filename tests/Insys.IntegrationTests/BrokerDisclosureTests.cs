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

    /// <summary>Tenant B is switched to customer-only, so tenant A's other tests are untouched.</summary>
    [Fact]
    public async Task SwitchedToCustomerOnly_TheBrokersAcknowledgementIsRefused_AndTheNoticeStillReads_OQ201_DE039()
    {
        var client = await Party(B, "Customer-only client");
        await _rows.Insert(B, "general.policy", ("policyholder_party_id", client), ("intermediary_code", "P39"), ("currency", "ZAR"));
        var broker = await Broker(B, "broker-39p-b", "P39");
        await fixture.Database.UserWithPermissions(B, "disclosure-admin-b", IntegrationPermissions.DisclosureRead, IntegrationPermissions.DisclosureSettingUpdate);
        var admin = _factory.ClientFor(B, "disclosure-admin-b");

        var setting = await admin.GetAsync("/api/v1/integration/disclosure-setting");
        var current = await setting.Json();
        (current.GetProperty("brokerAcknowledgementAllowed").GetBoolean(), current.GetProperty("isDefault").GetBoolean()).ShouldBe((true, true));
        (await admin.Send(HttpMethod.Put, "/api/v1/integration/disclosure-setting", new { })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the switch is named");
        var changed = await admin.Send(HttpMethod.Put, "/api/v1/integration/disclosure-setting", new { brokerAcknowledgementAllowed = false });
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        (await admin.Send(HttpMethod.Put, "/api/v1/integration/disclosure-setting", new { brokerAcknowledgementAllowed = true })).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed, "once set, a change needs If-Match");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ChangeDisclosureSetting' AND outcome = 'SUCCESS'",
            ("t", B))).ShouldBe(1);

        var refused = await broker.Send(HttpMethod.Post, Acknowledge, Body(client, "IN_PERSON"), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("DISCLOSURE_BROKER_NOT_ALLOWED");
        (await Count(B, client)).ShouldBe(0);
        var text = await broker.GetAsync(Text);
        text.StatusCode.ShouldBe(HttpStatusCode.OK, "the screen still shows the notice");
        (await text.Json()).GetProperty("brokerAcknowledgementAllowed").GetBoolean().ShouldBeFalse();

        // Tenant A, untouched, still allows it.
        var clientA = await Party(A, "Allowed client");
        await _rows.Insert(A, "general.policy", ("policyholder_party_id", clientA), ("intermediary_code", "P39"), ("currency", "ZAR"));
        (await (await Broker(A, "broker-39p-4", "P39")).Send(HttpMethod.Post, Acknowledge, Body(clientA, "IN_PERSON"), idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static object Body(Guid partyId, string? presentationMethod, string? presentationDetail = null, Guid? quote = null) => new
    {
        partyId, displayPoint = "ISSUE", noticeType = "POLICY_TERMS", jurisdiction = "ZA", language = "en",
        businessRefType = quote is null ? null : GeneralDocuments.QuoteRefType, businessRefId = quote, presentationMethod, presentationDetail,
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
