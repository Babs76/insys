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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Application.Ai;
using Insys.Identity.Api;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// Wave 7, batch 7.5: AI-1's review (AI Assist SRS §4.2, AIA-018 to AIA-020) through its routes, after a run of 7.4's job with the test stub as the model.
/// Sample wording and invented rates only; D-7's real documents aren't used (the evaluation waits for them).
/// </summary>
public sealed class AssistProductDraftReviewTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Drafts = "/api/v1/assist/product-drafts";

    private static readonly string[] Functions =
        ["ASSIST_PRODUCT_DRAFT_RUN", "PRODUCTS", "PRODUCT_CREATE_DRAFT_VERSION", "PRODUCT_EDIT_CONFIGURATION", "DOCUMENTS", "DOCUMENTS_STORE"];

    private static readonly string[][] Wording =
    [
        ["Sample Funeral Plan", "Cover: funeral benefit and accidental death benefit", "Cover starts on the first premium payment", "Cover ends at age 75",
            "A member aged 18 pays a monthly premium of 45.00"],
        ["Age band\tMonthly premium", "18-40\t45.00", "41-55\t62.50"],
    ];

    private sealed record World(Guid Tenant, InsysApiFactory Host, StubAiModelProvider Stub, HttpClient User, Guid UserId, string Product);

    private async Task<World> Ready(string subject, params string[] more)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "AR" + suffix);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
            VALUES (NULL, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', @tenant, 'AI_ASSIST', true)
            """, ("code", $"AR{suffix}:AI_ASSIST"), ("tenant", "AR" + suffix));
        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(tenant, "ar-admin", "ASSIST_SETTINGS_MANAGE");
        (await host.ClientFor(tenant, "ar-admin").Send(HttpMethod.Put, "/api/v1/assist/settings",
            new { jurisdiction = "ZA", inferenceRegion = "GLOBAL", legalBasis = "POPIA s72, binding agreement (sample)", explanationsEnabled = true })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var from = TestClock.Today.AddDays(-30);
        var product = "AR1-" + suffix;
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO product.cfg_product_definitions_and_versions (tenant_id, id, code, version, effective_from, record_status, created_by, change_reason,
                                                                     product_code, domain, name, coverages, cover_starts_on)
            VALUES (@t, gen_random_uuid(), @p, 1, @from, 'PUBLISHED', 'seed', 'published', @p, 'LIFE', 'Funeral (sample)', ARRAY['FUNERAL'], 'ISSUE')
            """, ("t", tenant), ("p", product), ("from", from));
        foreach (var code in new[] { "FUNERAL", "ACCIDENTAL_DEATH" })
        {
            await fixture.Database.SeedStatic(tenant, "product.cfg_coverage_and_benefit_library", "EFFECTIVE", code, from, ("type", "BENEFIT"), ("name", code),
                ("basis", "FIXED_BENEFIT"), ("measure", "SUM_ASSURED"), ("domain", "LIFE"));
        }

        await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "PRODUCT_DOC", from, ("category_code", "PRODUCT_DOC"),
            ("label", "Product document"), ("classification", "CONFIDENTIAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        var userId = await fixture.Database.UserWithFunctions(tenant, subject, [.. Functions, .. more]);
        var stub = (StubAiModelProvider)host.Services.GetRequiredService<IAiModelProvider>();
        stub.Unavailable = false;
        stub.Region = "us";
        return new World(tenant, host, stub, host.ClientFor(tenant, subject), userId, product);
    }

    private static object Cite(int page, string quote) => new { document = 1, page, quote };

    /// <summary>A drafted run: name Found (in the Draft), coverages and cover start Interpreted, a rate table, a rule and two simulation cases.</summary>
    private static async Task<JsonElement> Drafted(World w)
    {
        var stored = await w.User.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "PRODUCT_DOC", businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "application/pdf", fileName = "sample-wording",
            contentBase64 = Convert.ToBase64String(SampleDocuments.Pdf(Wording)),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        await w.Host.ScanUploads();
        var document = (await stored.Json()).GetProperty("id").GetGuid();
        w.Stub.Reply = request => request.Data.Contains(w.Product, StringComparison.Ordinal) ? JsonSerializer.Serialize(new
        {
            values = new object[]
            {
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = Cite(1, "Sample Funeral Plan") },
                new { field = "version.coverages", value = new[] { "FUNERAL", "ACCIDENTAL_DEATH" }, flag = "FOUND", citation = Cite(1, "funeral benefit and accidental death benefit") },
                new { field = "version.coverStartsOn", value = "FirstPayment", flag = "INTERPRETED", citation = Cite(1, "Cover starts on the first premium payment") },
                new { field = "version.reinstatementArrears", value = "Required", flag = "FOUND" },
            },
            rateTables = new object[]
            {
                new { citation = Cite(2, "Age band"), rows = new[] { new[] { "Age band", "Monthly premium" }, new[] { "18-40", "45.00" }, new[] { "41-55", "62.50" } } },
            },
            rules = new object[] { new { text = "Cover ends at age 75", citation = Cite(1, "Cover ends at age 75") } },
            simulationCases = new object[]
            {
                new
                {
                    description = "Youngest entry age", inputs = new { age = 18, currency = "ZAR", frequency = "MONTHLY" }, expected = new { premium = 45.00m },
                    citation = Cite(1, "A member aged 18 pays a monthly premium of 45.00"),
                },
                new { description = "Oldest entry age", inputs = new { age = 55, currency = "ZAR", frequency = "MONTHLY" } },
            },
        }) : "{}";
        var started = await w.User.Send(HttpMethod.Post, Drafts, new { productCode = w.Product, documents = new[] { new { documentId = document, confirmsNoPersonalData = true } } },
            idempotent: true);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var id = (await started.Json()).GetProperty("id").GetGuid();
        await w.Host.RunProductDrafts();
        var run = await (await w.User.GetAsync($"{Drafts}/{id}")).Json();
        run.GetProperty("status").GetString().ShouldBe("DRAFTED", run.ToString());
        return run;
    }

    private static JsonElement Item(JsonElement run, string field, bool passed = true) =>
        run.GetProperty("items").EnumerateArray().First(i => i.GetProperty("field").GetString() == field && (i.GetProperty("checkResult").GetString() == "PASSED") == passed);

    private static async Task<JsonElement> Reread(World w, JsonElement run, string field, bool passed = true) =>
        Item(await (await w.User.GetAsync($"{Drafts}/{run.GetProperty("id").GetGuid()}")).Json(), field, passed);

    private static Task<HttpResponseMessage> Decide(World w, JsonElement run, JsonElement item, string decision, object? value = null, string? note = null,
        long? version = null) =>
        w.User.Send(HttpMethod.Post, $"{Drafts}/{run.GetProperty("id").GetGuid()}/items/{item.GetProperty("id").GetGuid()}/decision",
            new { decision, value, note }, ifMatch: $"\"{version ?? item.GetProperty("rowVersion").GetInt64()}\"");

    private static async Task<JsonElement> Draft(World w) =>
        await (await w.User.GetAsync($"/api/v1/product-factory/products/{w.Product}/versions/2")).Json();

    [Fact]
    public async Task TheConfigurator_AcceptsEditsOrRejectsEachValue_TheEditRecordedAgainstTheProposal_AndTheDraftHoldsWhatWasDecided_AIA019()
    {
        var w = await Ready("ar-config");
        var run = await Drafted(w);

        // Accepting an Interpreted version field writes it to the Draft, as the user.
        var coverages = Item(run, "version.coverages");
        coverages.GetProperty("flag").GetString().ShouldBe("INTERPRETED");
        var accepted = await Decide(w, run, coverages, "ACCEPTED");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await Draft(w)).GetProperty("coverages").EnumerateArray().Select(c => c.GetString()).ShouldBe(["FUNERAL", "ACCIDENTAL_DEATH"]);

        // An edit records the person's value and why beside the AI's, which is kept, and the Draft takes the person's.
        var name = Item(run, "version.name");
        (await Decide(w, run, name, "EDITED", "Sample Funeral Plan Plus")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "an edit says why");
        var edited = await Decide(w, run, name, "EDITED", "Sample Funeral Plan Plus", "The brochure's full name");
        edited.StatusCode.ShouldBe(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        var editedItem = await edited.Json();
        editedItem.GetProperty("value").GetString().ShouldBe("\"Sample Funeral Plan\"", "the AI's proposal is never changed");
        editedItem.GetProperty("editedValue").GetString().ShouldBe("\"Sample Funeral Plan Plus\"");
        editedItem.GetProperty("decidedBy").GetString().ShouldBe(w.UserId.ToString());
        (await Draft(w)).GetProperty("name").GetString().ShouldBe("Sample Funeral Plan Plus");

        // Rejecting puts the field back to the version the Draft was copied from.
        var rejected = await Decide(w, run, await Reread(w, run, "version.name"), "REJECTED");
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());
        (await Draft(w)).GetProperty("name").GetString().ShouldBe("Funeral (sample)");

        // A person's value is checked as the field allows: only the domain's coverage codes.
        var notAllowed = await Decide(w, run, await Reread(w, run, "version.coverages"), "EDITED", new[] { "NOT_A_CODE" }, "typo");
        notAllowed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Draft(w)).GetProperty("coverages").EnumerateArray().Count().ShouldBe(2, "nothing written for a refused value");

        // If-Match on the item: a stale version is refused before anything is written.
        (await Decide(w, run, await Reread(w, run, "version.coverStartsOn"), "ACCEPTED", version: 999)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await Draft(w)).GetProperty("coverStartsOn").GetString().ShouldBe("Issue");

        // A dropped proposal is never decided; a rule or rate table is recorded only (entered in its own module).
        (await Decide(w, run, Item(run, "version.reinstatementArrears", passed: false), "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Decide(w, run, Item(run, "rule"), "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The run is its configurator's: another user, with the same functions, doesn't find it.
        await fixture.Database.UserWithFunctions(w.Tenant, "ar-other", Functions);
        var other = await w.Host.ClientFor(w.Tenant, "ar-other").Send(HttpMethod.Post,
            $"{Drafts}/{run.GetProperty("id").GetGuid()}/items/{Item(run, "rule").GetProperty("id").GetGuid()}/decision", new { decision = "REJECTED" }, ifMatch: "\"1\"");
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // IAM-006: every decision audited, with whether it wrote the Draft.
        await w.Host.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'DecideDraftItem'", ("t", w.Tenant)))
            .ShouldBe(4);
    }

    [Fact]
    public async Task TheProvenanceReport_ShowsTheApproverEveryValueItsCitationAndDecision_AndWhetherTheDraftStillHoldsIt_AIA020()
    {
        var w = await Ready("ar-maker");
        var run = await Drafted(w);
        (await Decide(w, run, Item(run, "version.coverages"), "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Decide(w, run, Item(run, "version.name"), "EDITED", "Sample Funeral Plan Plus", "The brochure's full name")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.Database.UserWithFunctions(w.Tenant, "ar-approver", "ASSIST_PRODUCT_DRAFT_PROVENANCE", "PRODUCTS");
        var approver = w.Host.ClientFor(w.Tenant, "ar-approver");
        var report = await (await approver.GetAsync($"{Drafts}/provenance/{w.Product}/2")).Json();
        report.GetProperty("runId").GetGuid().ShouldBe(run.GetProperty("id").GetGuid());
        var items = report.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(run.GetProperty("items").GetArrayLength(), "every proposed value, dropped ones included");
        var name = items.Single(i => i.GetProperty("field").GetString() == "version.name");
        name.GetProperty("outcome").GetString().ShouldBe("EDITED");
        name.GetProperty("citationFile").GetString().ShouldBe("sample-wording");
        name.GetProperty("citationQuote").GetString().ShouldBe("Sample Funeral Plan");
        name.GetProperty("matchesDraft").GetBoolean().ShouldBeTrue();
        items.Single(i => i.GetProperty("field").GetString() == "version.reinstatementArrears").GetProperty("outcome").GetString().ShouldBe("DROPPED");
        var summary = report.GetProperty("summary");
        summary.GetProperty("interpretedUndecided").GetInt32().ShouldBe(4, "cover start, the rule and both simulation cases: Interpreted, not yet reviewed (§8.1 step 5)");
        summary.GetProperty("reviewComplete").GetBoolean().ShouldBeFalse();

        // A change to the Draft outside the review shows: the approver sees the Draft no longer holds what was decided.
        var draft = await Draft(w);
        (await w.User.Send(HttpMethod.Put, $"/api/v1/product-factory/products/{w.Product}/versions/2",
            new { name = "Renamed outside", coverages = new[] { "FUNERAL", "ACCIDENTAL_DEATH" }, effectiveFrom = draft.GetProperty("effectiveFrom").GetString() },
            ifMatch: $"\"{draft.GetProperty("rowVersion").GetInt64()}\"")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var after = await (await approver.GetAsync($"{Drafts}/provenance/{w.Product}/2")).Json();
        after.GetProperty("summary").GetProperty("draftDiffers").GetInt32().ShouldBe(1);
        after.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("field").GetString() == "version.name").GetProperty("matchesDraft").GetBoolean().ShouldBeFalse();

        // Without the function: refused. A version AI-1 didn't draft: not found.
        await fixture.Database.UserWithFunctions(w.Tenant, "ar-reader", "PRODUCTS");
        (await w.Host.ClientFor(w.Tenant, "ar-reader").GetAsync($"{Drafts}/provenance/{w.Product}/2")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await approver.GetAsync($"{Drafts}/provenance/{w.Product}/1")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AcceptedSimulationCases_BecomeLinesForRatingsTestCaseLibrary_WhichAssistNeverWrites_AIA018()
    {
        var w = await Ready("ar-sim");
        var run = await Drafted(w);
        var cases = run.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "SIM_CASE").ToList();
        cases.Count.ShouldBe(2);
        foreach (var c in cases)
        {
            (await Decide(w, run, c, "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var export = await (await w.User.GetAsync($"{Drafts}/{run.GetProperty("id").GetGuid()}/simulation-cases")).Json();
        export.GetProperty("table").GetString().ShouldBe("rating.cfg_rating_test_case_library");
        var line = export.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("productVersion").GetString().ShouldBe(Insys.Rating.Domain.Calculations.RatingConfiguration.ProductVersionRef(w.Product, 2), "Rating's own reference");
        line.GetProperty("expectedPremium").GetDecimal().ShouldBe(45.00m);
        line.GetProperty("inputs").GetProperty("currency").GetString().ShouldBe("ZAR");
        export.GetProperty("notExported").EnumerateArray().Single().GetProperty("reason").GetString().ShouldBe("NO_EXPECTED_PREMIUM",
            "a case whose documents state no outcome isn't given one (AIA-018)");

        // §4.3, 0 bypasses: Assist writes nothing to Rating; the configurator imports the lines for a second person's approval.
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM rating.cfg_rating_test_case_library WHERE tenant_id = @t", ("t", w.Tenant))).ShouldBe(0);
    }

    /// <summary>
    /// OQ-190 (b), Ade 19 November 2026: Product Factory's submission refuses a version AI-1 drafted while an Interpreted value (here a version field, a rule and
    /// two simulation cases) isn't reviewed (§8.1 step 5); once the configurator has reviewed them, it submits. A version AI-1 didn't draft is unaffected.
    /// </summary>
    [Fact]
    public async Task Submission_RefusesAnAiDraftedVersion_UntilEveryInterpretedValueIsReviewed_ThenSubmits_OQ190()
    {
        var w = await Ready("ar-submit", "PRODUCT_VALIDATE_VERSION", "PRODUCT_SUBMIT_FOR_APPROVAL");
        await fixture.Database.SeedStatic(w.Tenant, "product.cfg_product_approval_workflow_settings", "EFFECTIVE", "AR-APPROVAL", TestClock.Today.AddDays(-30),
            ("tenant", "AR"), ("states_used", new[] { "DRAFT", "SUBMITTED", "APPROVED" }), ("approver_roles", new[] { "PRODUCT_MANAGER" }),
            ("simulation_required", false));
        var run = await Drafted(w);
        var url = $"/api/v1/product-factory/products/{w.Product}/versions/2";
        async Task Validate()
        {
            // An edit made by the review puts a validated version back to Draft; it is validated again before it is submitted.
            if ((await Draft(w)).GetProperty("status").GetString() == "Draft")
            {
                var validated = await w.User.Send(HttpMethod.Post, $"{url}/validate", ifMatch: (await w.User.GetAsync(url)).ETag());
                validated.StatusCode.ShouldBe(HttpStatusCode.NoContent, await validated.Content.ReadAsStringAsync());
            }
        }

        async Task<HttpResponseMessage> Submit() => await w.User.Send(HttpMethod.Post, $"{url}/submit", new { reason = "Launch" }, ifMatch: (await w.User.GetAsync(url)).ETag());

        await Validate();
        var refused = await Submit();
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("PRODUCT_AI_REVIEW_INCOMPLETE");
        (await Draft(w)).GetProperty("status").GetString().ShouldBe("Validated", "the refused submission changes nothing");

        // The configurator reviews each Interpreted value: the version fields, the rule and both simulation cases. Reviewing only some still refuses.
        // The coverages come back Interpreted from INSYS's check in this sample, so they are reviewed too.
        (await Decide(w, run, Item(run, "version.coverStartsOn"), "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Decide(w, run, Item(run, "version.coverages"), "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var rule = run.GetProperty("items").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "RULE");
        (await Decide(w, run, rule, "ACCEPTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Validate();
        var partly = await Submit();
        (await partly.Code()).ShouldBe("PRODUCT_AI_REVIEW_INCOMPLETE", "the simulation cases are Interpreted too, and must be reviewed (Ade, 19 November 2026)");

        foreach (var simulation in run.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "SIM_CASE"
                     && i.GetProperty("checkResult").GetString() == "PASSED" && i.GetProperty("flag").GetString() == "INTERPRETED"))
        {
            var fresh = await (await w.User.GetAsync($"{Drafts}/{run.GetProperty("id").GetGuid()}")).Json();
            var current = fresh.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == simulation.GetProperty("id").GetGuid());
            (await Decide(w, run, current, "REJECTED")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await Validate();
        var submitted = await Submit();
        submitted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await submitted.Content.ReadAsStringAsync());
        (await Draft(w)).GetProperty("status").GetString().ShouldBe("Submitted");
    }

    /// <summary>
    /// Ade, 19 November 2026: the provenance report's function sits under Product Factory's PRODUCTS screen, with no feature, in the product manager's template.
    /// A product manager reads an existing report once the tenant's AI Assist entitlement has ended; a user without the function is refused.
    /// </summary>
    [Fact]
    public async Task AProductManager_ReadsTheProvenanceReport_UnderProducts_WithAiAssistOff_AndAUserWithoutTheFunction_IsRefused_AIA020()
    {
        var w = await Ready("ar-drafter");
        var run = await Drafted(w);
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.cfg_feature_entitlements SET record_status = 'RETIRED' WHERE tenant_id IS NULL AND code = @c",
            ("c", $"AR{w.Product[4..]}:AI_ASSIST"));
        // Ended outside INSYS, so no event evicts the users' cached functions: evicted here, as the entitlement's own event would.
        w.Host.Services.GetRequiredService<Insys.Identity.Application.Users.IPermissionCache>().EvictTenant(w.Tenant);
        var off = await w.User.GetAsync($"{Drafts}/{run.GetProperty("id").GetGuid()}");
        off.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "AI Assist is off: the run's own function needs the feature");
        (await off.Code()).ShouldBe("FEATURE_NOT_ENTITLED");

        var manager = RoleTemplates.Expand(RoleTemplates.All.Single(t => t.Code == "PRODUCT_MANAGER"), FunctionRegistry.All()).Select(f => f.Code).ToArray();
        manager.ShouldContain("ASSIST_PRODUCT_DRAFT_PROVENANCE");
        await fixture.Database.UserWithFunctions(w.Tenant, "ar-manager", manager);
        var client = w.Host.ClientFor(w.Tenant, "ar-manager");
        var read = await client.GetAsync($"{Drafts}/provenance/{w.Product}/2");
        read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
        (await read.Json()).GetProperty("runId").GetGuid().ShouldBe(run.GetProperty("id").GetGuid());

        var menu = await (await client.GetAsync("/api/v1/identity/me/menu")).Json();
        var products = menu.GetProperty("domains").EnumerateArray().SelectMany(d => d.GetProperty("subDomains").EnumerateArray())
            .SelectMany(sd => sd.GetProperty("screens").EnumerateArray()).Single(sc => sc.GetProperty("functionCode").GetString() == "PRODUCTS");
        products.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("functionCode").GetString()).ShouldContain("ASSIST_PRODUCT_DRAFT_PROVENANCE");

        await fixture.Database.UserWithFunctions(w.Tenant, "ar-reader", "PRODUCTS");
        var refused = await w.Host.ClientFor(w.Tenant, "ar-reader").GetAsync($"{Drafts}/provenance/{w.Product}/2");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync());
        (await refused.Code()).ShouldBe("FUNCTION_NOT_GRANTED");
    }
}
