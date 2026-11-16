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
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// Wave 7, batch 7.7: the SETUP change set (AIA-085, AIA-086, D-11, D-12 option C). The completeness check, one approval for every batch, applied in
/// dependency order, the run following the set, and the activation guard. Sample and draft content only.
/// </summary>
public sealed class AssistSetupSubmissionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Setups = "/api/v1/assist/tenant-setups";

    private static JsonElement File(string path) =>
        JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "specs", "starter-packs", path))).RootElement.Clone();

    private static string Version(JsonElement v) => $"\"{v.GetProperty("rowVersion").GetInt64()}\"";

    private async Task Platform(params string[] files)
    {
        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-mgr", "ASSIST_OVERLAY_MANAGE");
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-lead", "ASSIST_OVERLAY_APPROVE");
        var manager = host.ClientFor(DatabaseFixture.Operator, "ss-mgr");
        var lead = host.ClientFor(DatabaseFixture.Operator, "ss-lead");
        foreach (var file in files)
        {
            var loaded = await manager.Send(HttpMethod.Post, "/api/v1/assist/overlays", File(file), idempotent: true);
            loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
            var view = await loaded.Json();
            var id = view.GetProperty("id").GetGuid();
            var reviewed = await manager.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/review",
                new { partner = "Sample Partners (sample)", reviewerName = "C. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
            (await lead.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: reviewed.ETag()))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private async Task<(Guid Tenant, Func<string, string[], Task<HttpClient>> User)> Customer()
    {
        var tenant = Guid.CreateVersion7();
        var code = "SS" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await fixture.Database.SeedTenant(tenant, code);
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ONBOARDING' WHERE id = @t", ("t", tenant));
        var host = await fixture.Host();
        return (tenant, async (subject, functions) =>
        {
            await fixture.Database.UserWithFunctions(tenant, subject, functions);
            return host.ClientFor(tenant, subject);
        });
    }

    /// <summary>Starts a run and accepts every value that isn't Missing.</summary>
    private static async Task<(Guid Id, JsonElement Run)> AcceptAll(HttpClient wizard, object answers)
    {
        var run = await (await wizard.Send(HttpMethod.Post, Setups, answers, idempotent: true)).Json();
        var id = run.GetProperty("id").GetGuid();
        foreach (var value in run.GetProperty("values").EnumerateArray().Where(v => v.GetProperty("flag").GetString() != "MISSING"))
        {
            (await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/items/{value.GetProperty("id").GetGuid()}/decision", new { decision = "ACCEPTED" }, ifMatch: Version(value)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return (id, await (await wizard.GetAsync($"{Setups}/{id}")).Json());
    }

    /// <summary>
    /// A small sample base pack whose rows load as they are: one reference table, one master table and two configuration tables, listed out of their dependency
    /// order. The codes carry a suffix, so a tenant that already holds the usual codes takes them too.
    /// </summary>
    private static JsonElement SamplePack(string suffix, params object[] extra)
    {
        object[] rows =
        [
            new { table = "iam.cfg_authentication_and_session_policies", code = "AUTH" + suffix, @class = "COMPLIANCE",
                values = new { policy_code = "AUTH" + suffix, session_timeout = "PT30M", lockout_attempts = 5, client_ids = Array.Empty<string>(), mfa_required = true,
                    mfa_required_roles = new[] { "TENANT_ADMIN" } } },
            new { table = "channel.cfg_channel_definitions", code = "CH-S" + suffix, @class = "OPERATIONAL",
                values = new { channel_code = "S" + suffix, audience = "STAFF", name = "Sample back office", client_ids = new[] { "insys-web" } } },
            new { table = "tenant.mst_organisation_structure", code = "RG" + suffix, @class = "OPERATIONAL",
                values = new { node_code = "RG" + suffix, type = "REGION", parent = "ROOT", name = "Sample region", licences = Array.Empty<string>() } },
            new { table = "claims.ref_reserve_types", code = "IND" + suffix, @class = "FINANCE", values = new { category = "INDEMNITY", posting_rule = "PR-4" } },
            .. extra,
        ];
        return JsonSerializer.SerializeToElement(new { pack = "base", kind = "BASE", status = "DRAFT", rows });
    }

    private async Task Platform(JsonElement pack)
    {
        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-mgr", "ASSIST_OVERLAY_MANAGE");
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-lead", "ASSIST_OVERLAY_APPROVE");
        var manager = host.ClientFor(DatabaseFixture.Operator, "ss-mgr");
        var lead = host.ClientFor(DatabaseFixture.Operator, "ss-lead");
        var loaded = await manager.Send(HttpMethod.Post, "/api/v1/assist/overlays", pack, idempotent: true);
        loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        var reviewed = await manager.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/review",
            new { partner = "Sample Partners (sample)", reviewerName = "C. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
        (await lead.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: reviewed.ETag()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static readonly string[] Approving = ["approval.read", "approval.approve", "static_data.approve", "change_request.read"];

    private async Task<HttpResponseMessage> Submit(HttpClient wizard, Guid id) =>
        await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/submit", null, idempotent: true, ifMatch: (await wizard.GetAsync($"{Setups}/{id}")).ETag());

    private async Task<Guid> SetApproval(Guid tenant, Guid setId) =>
        await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM workflow.approval WHERE tenant_id = @t AND business_ref_type = 'STATIC_DATA_SETUP' AND business_ref_id = @s",
            ("t", tenant), ("s", setId));

    private Task<string?> SetStatus(Guid tenant, Guid setId) =>
        fixture.Database.ScalarAsOwner<string>("SELECT status FROM sdm.setup_change_set WHERE tenant_id = @t AND id = @s", ("t", tenant), ("s", setId));

    [Fact]
    public async Task ASetup_IsOneSetupChangeSet_OneImportBatchPerTable_ApprovedOnceByASecondPerson_AndAppliedInDependencyOrder_AIA086()
    {
        await Platform(SamplePack("1"));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });

        var check = await (await wizard.GetAsync($"{Setups}/{id}/completeness")).Json();
        check.GetProperty("ready").GetBoolean().ShouldBeTrue(check.ToString());
        (check.GetProperty("tables").GetInt32(), check.GetProperty("values").GetInt32()).ShouldBe((4, 4));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.import_batch WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L, "the check saves nothing");

        var submitted = await Submit(wizard, id);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var view = await submitted.Json();
        view.GetProperty("status").GetString().ShouldBe("SUBMITTED");
        var setId = view.GetProperty("setId").GetGuid();
        view.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("table").GetString()).ShouldBe(
            ["claims.ref_reserve_types", "tenant.mst_organisation_structure", "channel.cfg_channel_definitions", "iam.cfg_authentication_and_session_policies"],
            "reference data, then master data, then configuration (AIA-086)");
        var host = await fixture.Host();
        await host.DrainOutbox();

        // One approval for the whole set, of Tier 1, bound to the set's hash; no approval of its own for any batch.
        (await SetStatus(tenant, setId)).ShouldBe("SUBMITTED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE tenant_id = @t", ("t", tenant))).ShouldBe(1L);
        (await fixture.Database.ScalarAsOwner<string>("SELECT approval_type || ' ' || (subject_hash = (SELECT hash FROM sdm.setup_change_set WHERE id = @s))::text FROM workflow.approval WHERE tenant_id = @t",
            ("t", tenant), ("s", setId))).ShouldBe("STATIC_DATA_T1 true");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND action = 'IMPORT' AND setup_set_id = @s AND status = 'PENDING_APPROVAL'",
            ("t", tenant), ("s", setId))).ShouldBe(4L);
        (await fixture.Database.ScalarAsOwner<string>("SELECT record_status FROM claims.ref_reserve_types WHERE tenant_id = @t AND code = 'IND1'", ("t", tenant))).ShouldBe("PENDING_APPROVAL");

        // Whoever ran the wizard can't approve it, whatever they hold (AIA-086).
        await fixture.Database.UserWithPermissions(tenant, "ss-runner", Approving);
        var approval = await SetApproval(tenant, setId);
        var bySelf = await wizard.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        (await bySelf.Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");

        // A ready tenant's approvals need authority: the seed's CLERK role decides every approval type (STATIC_DATA_T1 among them).
        await fixture.Database.AssignRole(tenant, await fixture.Database.UserWithPermissions(tenant, "ss-checker", Approving), "CLERK");
        var approved = await host.ClientFor(tenant, "ss-checker").Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        for (var i = 0; i < 10 && await SetStatus(tenant, setId) != "APPLIED"; i++)
        {
            await host.DrainOutbox();
        }

        (await SetStatus(tenant, setId)).ShouldBe("APPLIED");
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(table_name, ',' ORDER BY applied_at, seq) FROM sdm.setup_change_set_batch WHERE tenant_id = @t AND set_id = @s AND applied_at IS NOT NULL
            """, ("t", tenant), ("s", setId))).ShouldBe("claims.ref_reserve_types,tenant.mst_organisation_structure,channel.cfg_channel_definitions,iam.cfg_authentication_and_session_policies");
        (await fixture.Database.ScalarAsOwner<string>("SELECT record_status || ' ' || approved_by FROM claims.ref_reserve_types WHERE tenant_id = @t AND code = 'IND1'", ("t", tenant)))
            .ShouldStartWith("ACTIVE ");
        (await fixture.Database.ScalarAsOwner<string>("SELECT record_status FROM channel.cfg_channel_definitions WHERE tenant_id = @t AND code = 'CH-S1'", ("t", tenant))).ShouldBe("PUBLISHED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND setup_set_id = @s AND status = 'ACTIVE' AND checker_id IS NOT NULL",
            ("t", tenant), ("s", setId))).ShouldBe(4L);
        (await (await wizard.GetAsync($"{Setups}/{id}")).Json()).GetProperty("status").GetString().ShouldBe("APPLIED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ApplySetupBatch'", ("t", tenant))).ShouldBe(4L);
    }

    [Fact]
    public async Task ASetsApprover_HoldsTheApproveFunctionOfEveryModuleItTouches_AndMissingOneIsRefused_ADR026_SRS002_AIA086()
    {
        await Platform(SamplePack("7"));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });
        var setId = (await (await Submit(wizard, id)).Json()).GetProperty("setId").GetGuid();
        var host = await fixture.Host();
        await host.DrainOutbox();
        var approval = await SetApproval(tenant, setId);

        // The four tables' catalogue modules: claims framework, tenant management, portals and channels, identity and access management.
        string[] approves = ["SD_SHARED_CLAIMS_FRAMEWORK_APPROVE", "SD_TENANT_MANAGEMENT_APPROVE", "SD_PORTALS_AND_PARTNER_CHANNELS_APPROVE", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE"];
        var partial = await user("ss-partial", ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", .. approves[..3]]);
        var refused = await partial.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("FUNCTION_NOT_GRANTED");
        problem.GetProperty("detail").GetString()!.ShouldContain("SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE");
        (await SetStatus(tenant, setId)).ShouldBe("SUBMITTED");
        var listed = await (await partial.GetAsync("/api/v1/workflow/approvals?decidable=true")).Json();
        listed.ToString().ShouldNotContain(approval.ToString(), customMessage: "it isn't offered to them to decide either");

        var full = await user("ss-full", ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", .. approves]);
        var approved = await full.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Sample values for every field the corrected base and motor packs leave null (format-corrections-2026-11-16.md): the tenant's own overlay gives them
    /// in this test, approved by its compliance officer (the fallback, D-19). Sample data only; none is a real limit, route or calendar.
    /// </summary>
    private static readonly Dictionary<string, object> Samples = new()
    {
        ["target_system"] = "SAMPLE-LEDGER", ["schedule"] = "DAILY", ["holiday_calendar"] = "ZA", ["time_zone"] = "Africa/Johannesburg", ["appeal_route"] = "PPR_NFO",
        ["link_types"] = new[] { "POLICY" }, ["requires_category"] = false, ["limit_value"] = 1000, ["role_code"] = "APPROVER", ["max_sum_assured"] = 1000000,
        ["allowed_roles"] = new[] { "APPROVER" }, ["merge_fields"] = new[] { "policy.number" },
    };

    /// <summary>The pack rows with a null the corrections added, again, with the samples in place of their nulls; or every row, filled, when <paramref name="all"/>.</summary>
    private static List<System.Text.Json.Nodes.JsonObject> Filled(string file, bool all)
    {
        var rows = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var row in File(file).GetProperty("rows").EnumerateArray())
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(row.GetRawText())!.AsObject();
            var values = node["values"]!.AsObject();
            // Only the nulls the corrections added; an optional field left empty (the root node's parent) stays so.
            var nulls = values.Where(v => v.Value is null && Samples.ContainsKey(v.Key)).Select(v => v.Key).ToList();
            foreach (var key in nulls)
            {
                values[key] = JsonSerializer.SerializeToNode(Samples[key]);
                node["note"] = "Sample value for the test, in place of the pack's null.";
            }

            if (all || nulls.Count > 0)
            {
                rows.Add(node);
            }
        }

        return rows;
    }

    [Fact]
    public async Task TheFullBaseAndMotorSet_IsSubmittedApprovedAndApplied_EveryTableAfterTheTablesItRefersTo_AIA086_OQ178_OQ179()
    {
        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-mgr", "ASSIST_OVERLAY_MANAGE");
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "ss-lead", "ASSIST_OVERLAY_APPROVE");
        var manager = host.ClientFor(DatabaseFixture.Operator, "ss-mgr");
        var lead = host.ClientFor(DatabaseFixture.Operator, "ss-lead");
        // The platform approves the base for every tenant. The motor line is the tenant's own (the fallback, D-19): a value a platform line leaves empty can't
        // be given by a tenant while that line is approved (the country layer comes before the lines), so AM comes from the tenant's own motor version.
        foreach (var file in new[] { "base.json" })
        {
            var loaded = await manager.Send(HttpMethod.Post, "/api/v1/assist/overlays", File(file), idempotent: true);
            loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
            var view = await loaded.Json();
            var reviewed = await manager.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{view.GetProperty("id").GetGuid()}/review",
                new { partner = "Sample Partners (sample)", reviewerName = "C. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
            (await lead.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{view.GetProperty("id").GetGuid()}/approve", new { contentHash = view.GetProperty("contentHash").GetString() },
                ifMatch: reviewed.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var (tenant, user) = await Customer();
        var loader = await user("ss-loader", ["ASSIST_TENANT_SETUP_RUN"]);
        var officer = await user("ss-officer", ["ASSIST_OVERLAY_APPROVE_TENANT"]);
        var motor = File("overlays/line-general-motor.json");
        foreach (var pack in new[]
                 {
                     JsonSerializer.SerializeToElement(new { pack = "XQ", kind = "COUNTRY", status = "DRAFT", rows = Filled("base.json", all: false) }),
                     JsonSerializer.SerializeToElement(new { pack = motor.GetProperty("pack").GetString(), kind = "LINE", status = "DRAFT", rows = Filled("overlays/line-general-motor.json", all: true) }),
                 })
        {
            var own = await loader.Send(HttpMethod.Post, "/api/v1/assist/packs", pack, idempotent: true);
            own.StatusCode.ShouldBe(HttpStatusCode.Created, await own.Content.ReadAsStringAsync());
            var ownView = await own.Json();
            (await officer.Send(HttpMethod.Post, $"/api/v1/assist/packs/{ownView.GetProperty("id").GetGuid()}/approve", new { contentHash = ownView.GetProperty("contentHash").GetString() },
                ifMatch: own.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, run) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "GENERAL" } });
        run.GetProperty("missing").GetInt32().ShouldBe(0, "every value the packs leave empty comes from the tenant's approved overlay: "
            + string.Join(", ", run.GetProperty("values").EnumerateArray().Where(v => v.GetProperty("flag").GetString() == "MISSING")
                .Select(v => $"{v.GetProperty("table").GetString()} {v.GetProperty("code").GetString()} {v.GetProperty("missingReason").GetString()}")));
        var check = await (await wizard.GetAsync($"{Setups}/{id}/completeness")).Json();
        check.GetProperty("ready").GetBoolean().ShouldBeTrue(check.ToString());

        var submitted = await Submit(wizard, id);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var view7 = await submitted.Json();
        var setId = view7.GetProperty("setId").GetGuid();
        var order = view7.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("table").GetString()!).ToList();
        order.Count.ShouldBe(check.GetProperty("tables").GetInt32());
        foreach (var table in order)
        {
            foreach (var referred in Insys.BuildingBlocks.Application.StaticDataReferences.Refers.GetValueOrDefault(table, []).Where(order.Contains))
            {
                order.IndexOf(referred).ShouldBeLessThan(order.IndexOf(table), $"{table} refers to {referred}");
            }
        }

        await host.DrainOutbox();
        string[] approves = await fixture.Database.ScalarAsOwner<string[]>("SELECT array_agg(DISTINCT code ORDER BY code) FROM iam.ref_function_catalogue WHERE code ~ '^SD_.*_APPROVE$'") ?? [];
        var checker = await user("ss-checker", ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", .. approves]);
        var approved = await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await SetApproval(tenant, setId)}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        for (var i = 0; i < 3 * order.Count && await SetStatus(tenant, setId) != "APPLIED"; i++)
        {
            await host.DrainOutbox();
        }

        (await SetStatus(tenant, setId)).ShouldBe("APPLIED");
        (await fixture.Database.ScalarAsOwner<string>("SELECT string_agg(table_name, ',' ORDER BY applied_at, seq) FROM sdm.setup_change_set_batch WHERE tenant_id = @t AND set_id = @s",
            ("t", tenant), ("s", setId))).ShouldBe(string.Join(",", order), "applied in that order");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND setup_set_id = @s AND status = 'ACTIVE'", ("t", tenant), ("s", setId)))
            .ShouldBe(order.Count);
        (await fixture.Database.ScalarAsOwner<string>("SELECT record_status FROM rules.cfg_authority_matrices WHERE tenant_id = @t AND code = 'AM'", ("t", tenant)))
            .ShouldBe("PUBLISHED", "AM came only from the tenant's own approved motor version");
    }

    [Fact]
    public async Task ASetupWithABlocker_IsRefused_ASSIST_SETUP_NOT_READY_ListingEachBlocker_AndNothingIsLoaded_AIA085()
    {
        object unknownField = new { table = "billing.ref_billing_frequencies", code = "WEEKLY2", @class = "OPERATIONAL", values = new { label = "Weekly", periods_per_year = 52, colour = "blue" } };
        object wrongType = new { table = "billing.ref_billing_frequencies", code = "MONTHLY2", @class = "OPERATIONAL", values = new { label = "Monthly", periods_per_year = "twelve" } };
        object global = new { table = "refdata.ref_currencies", code = "XQD", @class = "OPERATIONAL", values = new { currency_code = "XQD", name = "Sample dollar", minor_units = 2 } };
        await Platform(SamplePack("2", unknownField, wrongType, global));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var run = await (await wizard.Send(HttpMethod.Post, Setups, new { country = "XQ", lines = new[] { "LIFE" } }, idempotent: true)).Json();
        var id = run.GetProperty("id").GetGuid();
        foreach (var value in run.GetProperty("values").EnumerateArray().Where(v => v.GetProperty("code").GetString() != "AUTH2"))
        {
            (await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/items/{value.GetProperty("id").GetGuid()}/decision",
                new { decision = value.GetProperty("code").GetString() == "RG2" ? "REJECTED" : "ACCEPTED" }, ifMatch: Version(value))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var check = await (await wizard.GetAsync($"{Setups}/{id}/completeness")).Json();
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        var blockers = check.GetProperty("blockers").EnumerateArray()
            .Select(b => $"{b.GetProperty("code").GetString()} {b.GetProperty("table").GetString()} {b.GetProperty("rowCode").GetString()} {b.GetProperty("field").GetString()}".TrimEnd()).ToList();
        blockers.ShouldBe([
            "UNDECIDED iam.cfg_authentication_and_session_policies AUTH2",
            "LINE_REFUSED billing.ref_billing_frequencies MONTHLY2 periods_per_year",
            "LINE_REFUSED billing.ref_billing_frequencies WEEKLY2 colour",
            "GLOBAL_TABLE refdata.ref_currencies",
        ], ignoreOrder: true, check.ToString());
        check.GetProperty("blockers").EnumerateArray().Single(b => b.GetProperty("field").GetString() == "colour").GetProperty("message").GetString().ShouldBe("The table has no such field.");
        check.GetProperty("warnings").EnumerateArray().Select(w => $"{w.GetProperty("code").GetString()} {w.GetProperty("rowCode").GetString()}").ShouldBe(["REJECTED RG2"]);
        check.ToString().ShouldNotContain("twelve", customMessage: "a finding names the field, never the value");

        var refused = await Submit(wizard, id);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Code()).ShouldBe("ASSIST_SETUP_NOT_READY");
        (await (await wizard.GetAsync($"{Setups}/{id}")).Json()).GetProperty("status").GetString().ShouldBe("DRAFT", "nothing is locked");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.import_batch WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM claims.ref_reserve_types WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L, "the check rolls back what it tried");
    }

    [Fact]
    public async Task OnlyWhoeverRanTheWizard_SubmitsIt_AndOnlyWhileTheTenantIsOnboarding_AIA086()
    {
        await Platform(SamplePack("3"));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var other = await user("ss-other", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });

        var byOther = await Submit(other, id);
        byOther.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await byOther.Code()).ShouldBe("FORBIDDEN");

        await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ACTIVE' WHERE id = @t", ("t", tenant));
        (await (await Submit(wizard, id)).Code()).ShouldBe("ASSIST_TENANT_LIVE");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.import_batch WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L);
    }

    [Fact]
    public async Task ASetupNotApproved_DiscardsItsDrafts_TheRunIsADraftAgain_AndItIsSubmittedAgainAsANewAttempt_AIA086()
    {
        await Platform(SamplePack("4"));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });
        var first = await (await Submit(wizard, id)).Json();
        var setId = first.GetProperty("setId").GetGuid();
        var host = await fixture.Host();
        await host.DrainOutbox();

        await fixture.Database.UserWithPermissions(tenant, "ss-checker", Approving);
        var checker = host.ClientFor(tenant, "ss-checker");
        var rejected = await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await SetApproval(tenant, setId)}/reject", new { reason = "Sample: the region is wrong" }, ifMatch: "\"1\"");
        rejected.StatusCode.ShouldBe(HttpStatusCode.NoContent, await rejected.Content.ReadAsStringAsync());
        for (var i = 0; i < 5; i++)
        {
            await host.DrainOutbox();
        }

        (await SetStatus(tenant, setId)).ShouldBe("NOT_APPROVED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND setup_set_id = @s AND status = 'REJECTED'", ("t", tenant), ("s", setId)))
            .ShouldBe(4L);
        foreach (var table in new[] { "claims.ref_reserve_types", "tenant.mst_organisation_structure", "channel.cfg_channel_definitions", "iam.cfg_authentication_and_session_policies" })
        {
            (await fixture.Database.ScalarAsOwner<long>($"SELECT count(*) FROM {table} WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L, $"{table}: the drafts are discarded");
        }

        var run = await (await wizard.GetAsync($"{Setups}/{id}")).Json();
        run.GetProperty("status").GetString().ShouldBe("DRAFT");

        var again = await Submit(wizard, id);
        again.StatusCode.ShouldBe(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        var second = await again.Json();
        second.GetProperty("attempt").GetInt32().ShouldBe(2);
        second.GetProperty("setId").GetGuid().ShouldNotBe(setId);
        second.GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("batchId").GetGuid())
            .ShouldNotContain(b => first.GetProperty("batches").EnumerateArray().Any(f => f.GetProperty("batchId").GetGuid() == b), "each attempt's batches are its own");
    }

    [Fact]
    public async Task ASubmissionThatStopsPartWay_StaysLocked_BlocksGoLive_AndIsWithdrawnBySubmitter_DiscardingWhatItLoaded_AIA086()
    {
        await Platform(SamplePack("6"));
        var (tenant, user) = await Customer();
        var wizard = await user("ss-runner", ["ASSIST_TENANT_SETUP_RUN"]);
        var other = await user("ss-other", ["ASSIST_TENANT_SETUP_RUN"]);
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });

        // The last table refuses this attempt's own batch, not the check's trial insert: the submission stops after loading the other three.
        var lastBatch = Insys.BuildingBlocks.Application.SetupOrder.BatchId(id, 1, "iam.cfg_authentication_and_session_policies");
        await fixture.Database.ExecuteAsOwner($"""
            CREATE FUNCTION iam.ss_refuse_batch() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.import_batch_id = '{lastBatch}' THEN RAISE EXCEPTION 'refused for the test' USING ERRCODE = 'check_violation'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER ss_refuse_batch BEFORE INSERT ON iam.cfg_authentication_and_session_policies FOR EACH ROW EXECUTE FUNCTION iam.ss_refuse_batch();
            """);
        try
        {
            var stopped = await Submit(wizard, id);
            stopped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await stopped.Content.ReadAsStringAsync());
            var run = await wizard.GetAsync($"{Setups}/{id}");
            (await run.Json()).GetProperty("status").GetString().ShouldBe("SUBMITTING", "the values stay locked; submitting again resumes");
            var host = await fixture.Host();
            await host.DrainOutbox();
            (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND setup_set_id IS NOT NULL AND status = 'PENDING_APPROVAL'",
                ("t", tenant))).ShouldBe(3L);
            var state = await host.Services.GetRequiredService<Insys.StaticData.Contracts.ISetupChangeSetState>().For(tenant, CancellationToken.None);
            state.Open.ShouldBe(1, "loaded batches with no set recorded yet still block go-live");

            // Only the submitter withdraws it; then what it loaded is discarded, and the setup is a draft again.
            var byOther = await other.Send(HttpMethod.Post, $"{Setups}/{id}/submission/withdraw", ifMatch: run.ETag());
            (await byOther.Code()).ShouldBe("FORBIDDEN");
            var withdrawn = await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/submission/withdraw", ifMatch: run.ETag());
            withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK, await withdrawn.Content.ReadAsStringAsync());
            (await withdrawn.Json()).GetProperty("status").GetString().ShouldBe("DRAFT");
            for (var i = 0; i < 5; i++)
            {
                await host.DrainOutbox();
            }

            (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM sdm.change_request WHERE tenant_id = @t AND setup_set_id IS NOT NULL AND status = 'REJECTED'", ("t", tenant)))
                .ShouldBe(3L);
            foreach (var table in new[] { "claims.ref_reserve_types", "tenant.mst_organisation_structure", "channel.cfg_channel_definitions" })
            {
                (await fixture.Database.ScalarAsOwner<long>($"SELECT count(*) FROM {table} WHERE tenant_id = @t", ("t", tenant))).ShouldBe(0L, $"{table}: discarded");
            }

            (await host.Services.GetRequiredService<Insys.StaticData.Contracts.ISetupChangeSetState>().For(tenant, CancellationToken.None)).Open.ShouldBe(0);
            (await fixture.Database.ScalarAsOwner<string>("SELECT status || ' ' || ending FROM sdm.setup_change_set WHERE tenant_id = @t", ("t", tenant))).ShouldBe("NOT_APPROVED WITHDRAWN");
        }
        finally
        {
            await fixture.Database.ExecuteAsOwner("DROP TRIGGER ss_refuse_batch ON iam.cfg_authentication_and_session_policies; DROP FUNCTION iam.ss_refuse_batch();");
        }
    }

    [Fact]
    public async Task TheTenantCantGoLive_WhileItsSetupChangeSetIsOpen_AndOnceApplied_EveryStepsReadinessRunsAgain_AIA085_AIA086()
    {
        var host = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "ss-operator", "tenant.read", "tenant.create", "tenant.update");
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "ss-operator-2", "tenant.read", "tenant.update");
        var op = host.ClientFor(DatabaseFixture.Operator, "ss-operator");
        var op2 = host.ClientFor(DatabaseFixture.Operator, "ss-operator-2");
        async Task<(Guid Tenant, string Url, string ETag)> Onboarded(string code)
        {
            var registered = await op.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
                new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
            registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
            var tenantId = (await registered.Json()).GetProperty("id").GetGuid();
            var url = $"/api/v1/tenancy/tenants/{tenantId}";
            await fixture.Database.SeedReadyTenant(tenantId, TestClock.Today.AddDays(-30));
            await fixture.Database.SeedActiveAdministrators(tenantId, code.ToLowerInvariant() + "-admin", "user.read");
            return (tenantId, url, await op.CompleteOnboarding(op2, url, (await op.GetAsync(url)).ETag()));
        }

        await Platform(SamplePack("5"));
        var (tenant, url, etag) = await Onboarded("SSGUARD");
        await fixture.Database.UserWithFunctions(tenant, "ss-runner", "ASSIST_TENANT_SETUP_RUN");
        var wizard = host.ClientFor(tenant, "ss-runner");
        var (id, _) = await AcceptAll(wizard, new { country = "XQ", lines = new[] { "LIFE" } });
        var submitted = await Submit(wizard, id);
        submitted.StatusCode.ShouldBe(HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        var setId = (await submitted.Json()).GetProperty("setId").GetGuid();
        await host.DrainOutbox();

        // Open: onboarding is complete, but activation refuses it, and readiness shows it.
        var open = await op.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag);
        open.StatusCode.ShouldBe(HttpStatusCode.Conflict, await open.Content.ReadAsStringAsync());
        (await open.Code()).ShouldBe("ONBOARDING_INCOMPLETE");
        var readiness = await (await op.GetAsync($"{url}/readiness")).Json();
        var check = readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == "END_TO_END_TESTS")
            .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "SETUP_CHANGE_SET");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing")[0].GetString()!.ShouldEndWith(" SUBMITTED");

        // A ready tenant's approvals need authority: the seed's CLERK role decides every approval type (STATIC_DATA_T1 among them).
        await fixture.Database.AssignRole(tenant, await fixture.Database.UserWithPermissions(tenant, "ss-checker", Approving), "CLERK");
        var approved = await host.ClientFor(tenant, "ss-checker").Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{await SetApproval(tenant, setId)}/approve", ifMatch: "\"1\"");
        approved.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approved.Content.ReadAsStringAsync());
        for (var i = 0; i < 10 && await SetStatus(tenant, setId) != "APPLIED"; i++)
        {
            await host.DrainOutbox();
        }

        (await SetStatus(tenant, setId)).ShouldBe("APPLIED");

        // Applied: every step's readiness runs again. A check that passed when its step was completed and fails now refuses go-live.
        await fixture.Database.ExecuteAsOwner("UPDATE finance.mst_chart_of_accounts SET effective_to = effective_from + 1 WHERE tenant_id = @t", ("t", tenant));
        var rechecked = await op.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag);
        rechecked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await rechecked.Content.ReadAsStringAsync());
        var problem = await rechecked.Json();
        problem.GetProperty("code").GetString().ShouldBe("ONBOARDING_STEP_NOT_READY");
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["FINANCIAL_FOUNDATIONS.CHART_OF_ACCOUNTS"], "the step was completed before the set; it is checked again");

        // Without a SETUP change set, activation looks only at the completed steps, as before.
        var (plain, plainUrl, plainETag) = await Onboarded("SSPLAIN");
        await fixture.Database.ExecuteAsOwner("UPDATE finance.mst_chart_of_accounts SET effective_to = effective_from + 1 WHERE tenant_id = @t", ("t", plain));
        (await op.Send(HttpMethod.Post, $"{plainUrl}/activate", new { reason = "Go live" }, ifMatch: plainETag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Readiness passes again: the tenant goes live.
        await fixture.Database.ExecuteAsOwner("UPDATE finance.mst_chart_of_accounts SET effective_to = NULL WHERE tenant_id = @t", ("t", tenant));
        var live = await op.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag);
        live.StatusCode.ShouldBe(HttpStatusCode.NoContent, await live.Content.ReadAsStringAsync());
    }
}
