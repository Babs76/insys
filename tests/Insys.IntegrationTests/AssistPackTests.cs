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
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// Wave 7, batch 7.6: the starter packs and the overlay lifecycle (ai-assist.md §5.2 and §5.3; D-18 to D-20; Ade, 15 November). Packs are loaded from the
/// repository's own pack files (docs/specs/starter-packs), sample and draft content only.
/// </summary>
public sealed class AssistPackTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Platform = "/api/v1/assist/overlays";
    private const string Tenant = "/api/v1/assist/packs";

    private static JsonElement File(string path) =>
        JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "specs", "starter-packs", path))).RootElement.Clone();

    private async Task<HttpClient> Operator(string subject, params string[] functions)
    {
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, subject, functions);
        return (await fixture.Host()).ClientFor(DatabaseFixture.Operator, subject);
    }

    private async Task<(Guid Tenant, Func<string, string[], Task<HttpClient>> User)> Customer(bool onboarding)
    {
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "PK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        if (onboarding)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ONBOARDING' WHERE id = @t", ("t", tenant));
        }

        var host = await fixture.Host();
        return (tenant, async (subject, functions) =>
        {
            await fixture.Database.UserWithFunctions(tenant, subject, functions);
            return host.ClientFor(tenant, subject);
        });
    }

    [Fact]
    public async Task APlatformVersion_IsLoaded_Reviewed_AndApprovedByTheLead_BoundToItsHash_AndEveryTenantReadsIt_S53()
    {
        var manager = await Operator("pk-mgr", "ASSIST_OVERLAY_MANAGE");
        var lead = await Operator("pk-lead", "ASSIST_OVERLAY_APPROVE");
        var loaded = await manager.Send(HttpMethod.Post, Platform, File("overlays/za.json"), idempotent: true);
        loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        (view.GetProperty("scope").GetString(), view.GetProperty("status").GetString(), view.GetProperty("country").GetString()).ShouldBe(("ALL_TENANTS", "DRAFT", "ZA"));

        var sheet = await (await manager.GetAsync($"{Platform}/{id}")).Json();
        var rows = sheet.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(view.GetProperty("rowCount").GetInt32());
        rows.ShouldAllBe(r => r.GetProperty("regulatedSource").GetString() == "INTERIM" && r.GetProperty("tailorableSource").GetString() == "DEFAULT",
            "no row of the drafts has its marks yet (D-20)");
        rows.Where(r => r.GetProperty("class").GetString() != "OPERATIONAL").ShouldAllBe(r => r.GetProperty("regulated").GetBoolean());
        rows.ShouldAllBe(r => !r.GetProperty("mayBeTailored").GetBoolean(), "an interim reading is never tailored");
        sheet.GetProperty("interimMarks").GetInt32().ShouldBe(rows.Count);

        (await lead.Send(HttpMethod.Post, $"{Platform}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: loaded.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict, "a version for every tenant needs the partner's review first");
        var reviewed = await manager.Send(HttpMethod.Post, $"{Platform}/{id}/review",
            new { partner = "Sample Compliance Partners (sample)", reviewerName = "A. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
        reviewed.StatusCode.ShouldBe(HttpStatusCode.OK, await reviewed.Content.ReadAsStringAsync());

        (await (await lead.GetAsync($"{Platform}/awaiting-approval")).Json()).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldContain(id);
        var toApprove = await lead.GetAsync($"{Platform}/{id}/approval");
        var wrong = await lead.Send(HttpMethod.Post, $"{Platform}/{id}/approve", new { contentHash = new string('0', 64) }, ifMatch: toApprove.ETag());
        (wrong.StatusCode, await wrong.Code()).ShouldBe((HttpStatusCode.Conflict, "ASSIST_PACK_CHANGED"));
        var approved = await lead.Send(HttpMethod.Post, $"{Platform}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: toApprove.ETag());
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        (await approved.Json()).GetProperty("status").GetString().ShouldBe("APPROVED");

        var (_, user) = await Customer(onboarding: true);
        var reader = await user("pk-reader", ["ASSIST_TENANT_SETUP_RUN"]);
        (await (await reader.GetAsync($"{Tenant}/{id}")).Json()).GetProperty("pack").GetProperty("status").GetString().ShouldBe("APPROVED", "every tenant reads a platform version");
        await (await fixture.Host()).DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(DISTINCT operation) FROM audit.audit_event WHERE target_id = @id AND operation IN ('LoadStarterPack', 'MarkStarterPackReviewed', 'ApproveStarterPack')",
            ("id", id.ToString()))).ShouldBe(3L, "every step is audited");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhoeverLoadedOrReviewedAVersion_CantApproveIt_EvenHoldingTheApprovalFunction_S53(bool reviewerApproves)
    {
        var both = await Operator($"pk-both-{reviewerApproves}", "ASSIST_OVERLAY_MANAGE", "ASSIST_OVERLAY_APPROVE");
        var other = await Operator($"pk-other-{reviewerApproves}", "ASSIST_OVERLAY_MANAGE");
        var loader = reviewerApproves ? other : both;
        var loaded = await loader.Send(HttpMethod.Post, Platform, File("overlays/line-general-motor.json"), idempotent: true);
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        var reviewer = reviewerApproves ? both : other;
        var reviewed = await reviewer.Send(HttpMethod.Post, $"{Platform}/{id}/review",
            new { partner = "Sample Partners (sample)", reviewerName = "B. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
        reviewed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await both.Send(HttpMethod.Post, $"{Platform}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: reviewed.ETag());
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Forbidden, "MAKER_CHECKER_SAME_PERSON"),
            reviewerApproves ? "the reviewer can't approve" : "the loader can't approve");
    }

    [Fact]
    public async Task ManagingOverlays_NeverGivesTheApproval_S53()
    {
        var manager = await Operator("pk-only-mgr", "ASSIST_OVERLAY_MANAGE");
        (await manager.GetAsync($"{Platform}/awaiting-approval")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var loaded = await manager.Send(HttpMethod.Post, Platform, File("base.json"), idempotent: true);
        (await manager.Send(HttpMethod.Post, $"{Platform}/{(await loaded.Json()).GetProperty("id").GetGuid()}/approve", new { contentHash = new string('0', 64) }, ifMatch: loaded.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ATenantsOwnVersion_IsLoadedWhileOnboarding_ApprovedByItsComplianceOfficer_NotByItsLoader_AndSeenByNoOtherTenant_D19()
    {
        var (_, user) = await Customer(onboarding: true);
        var loader = await user("pk-t-loader", ["ASSIST_TENANT_SETUP_RUN", "ASSIST_OVERLAY_APPROVE_TENANT"]);
        var officer = await user("pk-t-officer", ["ASSIST_OVERLAY_APPROVE_TENANT"]);
        var loaded = await loader.Send(HttpMethod.Post, Tenant, File("overlays/ng.json"), idempotent: true);
        loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        (view.GetProperty("scope").GetString(), view.GetProperty("status").GetString()).ShouldBe(("TENANT_ONLY", "DRAFT"));

        var refused = await loader.Send(HttpMethod.Post, $"{Tenant}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: loaded.ETag());
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Forbidden, "MAKER_CHECKER_SAME_PERSON"), "whoever loaded it can't approve it");
        var approved = await officer.Send(HttpMethod.Post, $"{Tenant}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: loaded.ETag());
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        (await approved.Json()).GetProperty("status").GetString().ShouldBe("APPROVED");

        var (_, another) = await Customer(onboarding: true);
        var stranger = await another("pk-t-stranger", ["ASSIST_TENANT_SETUP_RUN"]);
        (await stranger.GetAsync($"{Tenant}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "only that tenant can use it");
        var lead = await Operator("pk-t-lead", "ASSIST_OVERLAY_APPROVE");
        (await lead.GetAsync($"{Platform}/{id}/approval")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "nor the platform");
    }

    [Fact]
    public async Task ALiveTenant_CantLoadItsOwnVersion_D19()
    {
        var (_, user) = await Customer(onboarding: false);
        var loader = await user("pk-live-loader", ["ASSIST_TENANT_SETUP_RUN"]);
        var refused = await loader.Send(HttpMethod.Post, Tenant, File("overlays/ng.json"), idempotent: true);
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Conflict, "ASSIST_TENANT_LIVE"), await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExplicitMarks_AreTakenAsGiven_AndOnlyThoseMakeARowTailorable_D20()
    {
        var manager = await Operator("pk-marks", "ASSIST_OVERLAY_MANAGE");
        var file = new
        {
            pack = "MARKS_TEST", kind = "LINE",
            rows = new object[]
            {
                new { table = "general.cfg_sample", code = "A", @class = "OPERATIONAL", regulated = false, tailorable = true,
                    tailor = new { columns = new[] { "a" }, from = new[] { "timeZone" } }, values = new { b = 2, a = 1 } },
                new { table = "general.cfg_sample", code = "B", @class = "FINANCE", values = new { rate = 0.15 } },
                new { table = "general.cfg_sample", code = "C", @class = "COMPLIANCE", regulated = true, tailorable = true, values = new { text = "Sample" } },
            },
        };
        var loaded = await manager.Send(HttpMethod.Post, Platform, file, idempotent: true);
        loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
        var rows = (await (await manager.GetAsync($"{Platform}/{(await loaded.Json()).GetProperty("id").GetGuid()}")).Json()).GetProperty("rows").EnumerateArray().ToList();
        rows.Select(r => (r.GetProperty("code").GetString(), r.GetProperty("regulated").GetBoolean(), r.GetProperty("regulatedSource").GetString(), r.GetProperty("tailorable").GetBoolean(),
            r.GetProperty("tailorableSource").GetString(), r.GetProperty("mayBeTailored").GetBoolean())).ShouldBe(
        [
            ("A", false, "EXPLICIT", true, "EXPLICIT", true),
            ("B", true, "INTERIM", false, "DEFAULT", false),
            ("C", true, "EXPLICIT", true, "EXPLICIT", false),
        ], "a regulated value is never tailored, and the interim reading never makes a row tailorable");

        var bad = await manager.Send(HttpMethod.Post, Platform, new { pack = "BAD", kind = "LINE", rows = new[] { new { table = "t", code = "X", @class = "OPERATIONAL", regulated = "yes", values = new { } } } },
            idempotent: true);
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "a mark is true or false");
    }

    [Fact]
    public async Task APacksRows_CantBeChanged_OnlyAdded()
    {
        await fixture.Host();
        foreach (var privilege in new[] { "UPDATE", "DELETE" })
        {
            (await fixture.Database.ScalarAsOwner<bool>($"SELECT has_table_privilege('insys_assist', 'assist.starter_pack_value', '{privilege}')")).ShouldBeFalse(privilege);
        }
    }

    private static object Review => new { partner = "Sample Partners (sample)", reviewerName = "D. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() };

    private static object Line(string code) => new { pack = code, kind = "LINE", rows = new[] { new { table = "general.cfg_sample", code = "A", @class = "OPERATIONAL", values = new { x = 1 } } } };

    [Fact]
    public async Task ReloadingByAnotherPerson_DoesntMakeTheFirstLoaderAnApprover_AndOnlyTheNewestVersionIsApproved_AndOnlyOnce_S53()
    {
        var a = await Operator("pk-rl-a", "ASSIST_OVERLAY_MANAGE", "ASSIST_OVERLAY_APPROVE");
        var b = await Operator("pk-rl-b", "ASSIST_OVERLAY_MANAGE");
        var lead = await Operator("pk-rl-lead", "ASSIST_OVERLAY_APPROVE");
        var v1 = await a.Send(HttpMethod.Post, Platform, Line("RELOAD_LINE"), idempotent: true);
        var v1View = await v1.Json();
        var v2 = await b.Send(HttpMethod.Post, Platform, Line("RELOAD_LINE"), idempotent: true);
        var v2View = await v2.Json();
        v2View.GetProperty("version").GetInt32().ShouldBe(2);

        var r1 = await b.Send(HttpMethod.Post, $"{Platform}/{v1View.GetProperty("id").GetGuid()}/review", Review, ifMatch: v1.ETag());
        var old = await lead.Send(HttpMethod.Post, $"{Platform}/{v1View.GetProperty("id").GetGuid()}/approve", new { contentHash = v1View.GetProperty("contentHash").GetString() }, ifMatch: r1.ETag());
        (old.StatusCode, await old.Code()).ShouldBe((HttpStatusCode.Conflict, "ASSIST_PACK_NOT_NEWEST"), "a correction isn't overtaken by the version it corrects");

        var r2 = await lead.Send(HttpMethod.Post, $"{Platform}/{v2View.GetProperty("id").GetGuid()}/review", Review, ifMatch: v2.ETag());
        r2.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the lead holds no ASSIST_OVERLAY_MANAGE");
        r2 = await b.Send(HttpMethod.Post, $"{Platform}/{v2View.GetProperty("id").GetGuid()}/review", Review, ifMatch: v2.ETag());
        var byA = await a.Send(HttpMethod.Post, $"{Platform}/{v2View.GetProperty("id").GetGuid()}/approve", new { contentHash = v2View.GetProperty("contentHash").GetString() }, ifMatch: r2.ETag());
        (byA.StatusCode, await byA.Code()).ShouldBe((HttpStatusCode.Forbidden, "MAKER_CHECKER_SAME_PERSON"), "A loaded the pack's first version");

        var c = await Operator("pk-rl-c", "ASSIST_OVERLAY_MANAGE");
        var v3 = await c.Send(HttpMethod.Post, Platform, Line("RELOAD_LINE"), idempotent: true);
        var v3View = await v3.Json();
        var r3 = await b.Send(HttpMethod.Post, $"{Platform}/{v3View.GetProperty("id").GetGuid()}/review", Review, ifMatch: v3.ETag());
        var ok = await lead.Send(HttpMethod.Post, $"{Platform}/{v3View.GetProperty("id").GetGuid()}/approve", new { contentHash = v3View.GetProperty("contentHash").GetString() }, ifMatch: r3.ETag());
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        var again = await lead.Send(HttpMethod.Post, $"{Platform}/{v3View.GetProperty("id").GetGuid()}/approve", new { contentHash = v3View.GetProperty("contentHash").GetString() }, ifMatch: ok.ETag());
        (again.StatusCode, await again.Code()).ShouldBe((HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION"));
    }

    [Fact]
    public async Task AVersionIsApprovedOnlyThroughItsOwnRoute_AndItsContentCantChangeInTheDatabase_S53()
    {
        var manager = await Operator("pk-rt-mgr", "ASSIST_OVERLAY_MANAGE");
        var loaded = await manager.Send(HttpMethod.Post, Platform, Line("ROUTE_LINE"), idempotent: true);
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        var (_, user) = await Customer(onboarding: true);
        var officer = await user("pk-rt-officer", ["ASSIST_OVERLAY_APPROVE_TENANT"]);
        (await officer.Send(HttpMethod.Post, $"{Tenant}/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: loaded.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "a tenant's officer approves only its own tenant's versions");

        await Should.ThrowAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsOwner("UPDATE assist.starter_pack SET content_hash = repeat('0', 64) WHERE id = @i", ("i", id)));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsOwner("UPDATE assist.starter_pack SET scope = 'TENANT_ONLY' WHERE id = @i", ("i", id)));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsOwner("""
            INSERT INTO assist.starter_pack_value (tenant_id, id, created_at, created_by, pack_id, scope, line_no, table_name, row_code, class, regulated, regulated_source, tailorable,
              tailorable_source, values)
            SELECT tenant_id, gen_random_uuid(), now(), 'test', id, scope, 99, 'x', 'Y', 'FINANCE', true, 'EXPLICIT', false, 'DEFAULT', '{}'::jsonb FROM assist.starter_pack WHERE id = @i
            """, ("i", id)));
    }

    [Fact]
    public async Task WhoeverRanASetupWithAVersion_CantApproveIt_S53()
    {
        var (_, user) = await Customer(onboarding: true);
        var loader = await user("pk-ws-loader", ["ASSIST_TENANT_SETUP_RUN"]);
        var runner = await user("pk-ws-runner", ["ASSIST_TENANT_SETUP_RUN", "ASSIST_OVERLAY_APPROVE_TENANT"]);
        var loaded = await loader.Send(HttpMethod.Post, Tenant, new { pack = "XW", kind = "COUNTRY", rows = new[] { new { table = "t", code = "A", @class = "OPERATIONAL", values = new { x = 1 } } } },
            idempotent: true);
        var view = await loaded.Json();
        (await runner.Send(HttpMethod.Post, "/api/v1/assist/tenant-setups", new { country = "XW", lines = new[] { "GENERAL" } }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var refused = await runner.Send(HttpMethod.Post, $"{Tenant}/{view.GetProperty("id").GetGuid()}/approve", new { contentHash = view.GetProperty("contentHash").GetString() },
            ifMatch: loaded.ETag());
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Forbidden, "MAKER_CHECKER_SAME_PERSON"), "whoever ran the wizard with it can't approve it");
    }
}

