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
/// Wave 7, batch 7.6: AI-3's pack-only path (AI Assist SRS §5A.3; AIA-081, AIA-082, AIA-084, AIA-086, AIA-089, AIA-090), on the repository's own base, NG and
/// motor packs. Sample and draft content only.
/// </summary>
public sealed class AssistSetupTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Setups = "/api/v1/assist/tenant-setups";

    private static JsonElement File(string path) =>
        JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "specs", "starter-packs", path))).RootElement.Clone();

    private Guid? _base, _motor, _ng;

    /// <summary>The platform's packs, once: base and the motor line approved after review, NG left a draft.</summary>
    private async Task Platform()
    {
        if (_base is not null)
        {
            return;
        }

        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "su-mgr", "ASSIST_OVERLAY_MANAGE");
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "su-lead", "ASSIST_OVERLAY_APPROVE");
        var manager = host.ClientFor(DatabaseFixture.Operator, "su-mgr");
        var lead = host.ClientFor(DatabaseFixture.Operator, "su-lead");
        async Task<Guid> Load(string file, bool approve)
        {
            var loaded = await manager.Send(HttpMethod.Post, "/api/v1/assist/overlays", File(file), idempotent: true);
            loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
            var view = await loaded.Json();
            var id = view.GetProperty("id").GetGuid();
            if (approve)
            {
                var reviewed = await manager.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/review",
                    new { partner = "Sample Partners (sample)", reviewerName = "C. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
                (await lead.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/approve", new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: reviewed.ETag()))
                    .StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            return id;
        }

        _base = await Load("base.json", approve: true);
        _motor = await Load("overlays/line-general-motor.json", approve: true);
        _ng = await Load("overlays/ng.json", approve: false);
    }

    private async Task<(Guid Tenant, Func<string, string[], Task<HttpClient>> User)> Customer(bool onboarding = true, bool aiEntitled = true)
    {
        var tenant = Guid.CreateVersion7();
        var code = "SU" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await fixture.Database.SeedTenant(tenant, code);
        if (onboarding)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ONBOARDING' WHERE id = @t", ("t", tenant));
        }

        if (!aiEntitled)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_feature_entitlements SET enabled = false WHERE tenant = @c AND feature = 'AI_ASSIST'", ("c", code));
        }

        var host = await fixture.Host();
        return (tenant, async (subject, functions) =>
        {
            await fixture.Database.UserWithFunctions(tenant, subject, functions);
            return host.ClientFor(tenant, subject);
        });
    }

    private static IEnumerable<JsonElement> Values(JsonElement run) => run.GetProperty("values").EnumerateArray();

    [Fact]
    public async Task ThePackOnlyPath_AssemblesTheDraft_FromBaseCountryAndLine_WithNoModel_AndARegulatedValueFromAnUnapprovedOverlayIsMissing_AIA082_AIA090()
    {
        await Platform();
        var (_, user) = await Customer(aiEntitled: false);
        var wizard = await user("su-wizard", ["ASSIST_TENANT_SETUP_RUN"]);
        var started = await wizard.Send(HttpMethod.Post, Setups, new { country = "NG", lines = new[] { "GENERAL" }, currencies = new[] { "NGN" }, timeZone = "Africa/Lagos" },
            idempotent: true);
        started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        var run = await started.Json();

        run.GetProperty("packIds").EnumerateArray().Select(p => p.GetGuid()).ShouldBe([_base!.Value, _ng!.Value, _motor!.Value], "base, then the country, then the line (AIA-089)");
        var values = Values(run).ToList();
        var fromNg = values.Where(v => v.GetProperty("sourcePackId").GetGuid() == _ng).ToList();
        fromNg.Where(v => v.GetProperty("regulated").GetBoolean())
            .ShouldAllBe(v => v.GetProperty("flag").GetString() == "MISSING" && v.GetProperty("missingReason").GetString() == "NOT_APPROVED" && v.GetProperty("values").ValueKind == JsonValueKind.Null,
                "NG's overlay isn't approved, so none of its regulated values is filled");
        fromNg.Where(v => !v.GetProperty("regulated").GetBoolean()).ShouldAllBe(v => v.GetProperty("flag").GetString() == "PACK");
        values.Where(v => v.GetProperty("sourcePackId").GetGuid() == _motor).ShouldNotBeEmpty();
        values.ShouldAllBe(v => v.GetProperty("flag").GetString() == "PACK" || v.GetProperty("flag").GetString() == "MISSING", "no model ran, so nothing is Tailored or Mapped");
        run.GetProperty("notApproved").GetInt32().ShouldBe(fromNg.Count(v => v.GetProperty("regulated").GetBoolean()));
        run.GetProperty("missing").GetInt32().ShouldBe(values.Count(v => v.GetProperty("flag").GetString() == "MISSING"));
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM assist.run")).ShouldBe(0L, "the pack-only path calls no model");
    }

    [Fact]
    public async Task AValue_IsAcceptedOrRejected_AndAMissingOneCantBeAccepted_AndNewAnswersRedraftOnlyWhatTheyChange_AIA081()
    {
        await Platform();
        var (_, user) = await Customer();
        var wizard = await user("su-decider", ["ASSIST_TENANT_SETUP_RUN"]);
        var run = await (await wizard.Send(HttpMethod.Post, Setups, new { country = "NG", lines = new[] { "GENERAL" } }, idempotent: true)).Json();
        var id = run.GetProperty("id").GetGuid();
        var pack = Values(run).First(v => v.GetProperty("flag").GetString() == "PACK" && v.GetProperty("sourcePackId").GetGuid() == _base);
        var missing = Values(run).First(v => v.GetProperty("missingReason").ValueKind == JsonValueKind.String && v.GetProperty("missingReason").GetString() == "NOT_APPROVED");
        string Version(JsonElement v) => $"\"{v.GetProperty("rowVersion").GetInt64()}\"";

        (await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/items/{pack.GetProperty("id").GetGuid()}/decision", new { decision = "ACCEPTED" }, ifMatch: Version(pack)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await wizard.Send(HttpMethod.Post, $"{Setups}/{id}/items/{missing.GetProperty("id").GetGuid()}/decision", new { decision = "ACCEPTED" }, ifMatch: Version(missing));
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.UnprocessableEntity, "ASSIST_PACK_NOT_APPROVED"));

        var current = await wizard.GetAsync($"{Setups}/{id}");
        var reanswered = await wizard.Send(HttpMethod.Put, $"{Setups}/{id}/answers", new { country = "ZA", lines = new[] { "GENERAL" } }, ifMatch: current.ETag());
        reanswered.StatusCode.ShouldBe(HttpStatusCode.OK, await reanswered.Content.ReadAsStringAsync());
        var after = await reanswered.Json();
        after.GetProperty("answersVersion").GetInt32().ShouldBe(2);
        after.GetProperty("packIds").EnumerateArray().Select(p => p.GetGuid()).ShouldBe([_base!.Value, _motor!.Value], "no ZA overlay is loaded here");
        var kept = Values(after).Single(v => v.GetProperty("id").GetGuid() == pack.GetProperty("id").GetGuid());
        (kept.GetProperty("decision").GetString(), kept.GetProperty("answersVersion").GetInt32()).ShouldBe(("ACCEPTED", 1), "an unchanged value keeps its decision");
        (await fixture.Database.ScalarAsOwner<string>("SELECT decision FROM assist.setup_item WHERE id = @i", ("i", missing.GetProperty("id").GetGuid())))
            .ShouldBe("REJECTED", "a value the new answers no longer bring is rejected");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM assist.setup_answer WHERE run_id = @r", ("r", id))).ShouldBe(2L, "each answers version is kept");
    }

    [Fact]
    public async Task ATenantsOwnApprovedOverlay_SuppliesItsRegulatedValues_AndEmptyOnesStayMissing_D19()
    {
        await Platform();
        var (_, user) = await Customer();
        var wizard = await user("su-own", ["ASSIST_TENANT_SETUP_RUN"]);
        var officer = await user("su-officer", ["ASSIST_OVERLAY_APPROVE_TENANT"]);
        var loaded = await wizard.Send(HttpMethod.Post, "/api/v1/assist/packs", File("overlays/ng.json"), idempotent: true);
        var own = await loaded.Json();
        (await officer.Send(HttpMethod.Post, $"/api/v1/assist/packs/{own.GetProperty("id").GetGuid()}/approve", new { contentHash = own.GetProperty("contentHash").GetString() },
            ifMatch: loaded.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);

        var run = await (await wizard.Send(HttpMethod.Post, Setups, new { country = "NG", lines = new[] { "GENERAL" } }, idempotent: true)).Json();
        run.GetProperty("packIds").EnumerateArray().Select(p => p.GetGuid()).ShouldContain(own.GetProperty("id").GetGuid(), "its own approved version, over the platform's draft");
        var regulated = Values(run).Where(v => v.GetProperty("sourcePackId").GetGuid() == own.GetProperty("id").GetGuid() && v.GetProperty("regulated").GetBoolean()).ToList();
        regulated.ShouldNotBeEmpty();
        regulated.ShouldAllBe(v => v.GetProperty("missingReason").ValueKind != JsonValueKind.String || v.GetProperty("missingReason").GetString() == "NO_VALUE",
            "approved, so a value is filled where the pack gives one; the skeleton's empty ones stay Missing");
        run.GetProperty("notApproved").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task ALiveTenant_CantRunTheWizard_AIA086()
    {
        var (_, user) = await Customer(onboarding: false);
        var wizard = await user("su-live", ["ASSIST_TENANT_SETUP_RUN"]);
        var refused = await wizard.Send(HttpMethod.Post, Setups, new { country = "NG", lines = new[] { "GENERAL" } }, idempotent: true);
        (refused.StatusCode, await refused.Code()).ShouldBe((HttpStatusCode.Conflict, "ASSIST_TENANT_LIVE"));
    }

    [Fact]
    public async Task AnswersWithoutACountryOrWithAnUnknownLine_AreRefused()
    {
        var (_, user) = await Customer();
        var wizard = await user("su-bad", ["ASSIST_TENANT_SETUP_RUN"]);
        (await wizard.Send(HttpMethod.Post, Setups, new { country = "NGA", lines = new[] { "GENERAL" } }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await wizard.Send(HttpMethod.Post, Setups, new { country = "NG", lines = new[] { "MARINE" } }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task AnswersAreKept_OnlyAddedTo()
    {
        await fixture.Host();
        foreach (var privilege in new[] { "UPDATE", "DELETE" })
        {
            (await fixture.Database.ScalarAsOwner<bool>($"SELECT has_table_privilege('insys_assist', 'assist.setup_answer', '{privilege}')")).ShouldBeFalse(privilege);
        }
    }
}
