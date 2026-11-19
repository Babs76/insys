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
using Insys.BuildingBlocks.Application.Ai;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>
/// Wave 7, batch 7.8: AI-3's tailoring and document mapping (AI Assist SRS §5A.3; AIA-080 to AIA-084, AIA-087, AIA-090; D-16; OQ-186 to OQ-188, Ade 19 November
/// 2026), through its routes and job, with the test stub as the model. A small base pack of its own, with one row marked tailorable; the tenant's documents are
/// made in code, invented sample data only.
/// </summary>
public sealed class AssistSetupAiTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Setups = "/api/v1/assist/tenant-setups";
    private const string Calendars = "workflow.ref_business_calendars";
    private const string Accounts = "finance.mst_chart_of_accounts";
    private const string Authorities = "workflow.cfg_delegation_and_approval_authority";
    private const string Roles = "iam.cfg_roles_and_permission_sets";

    private static readonly string[] WizardFunctions = ["ASSIST_TENANT_SETUP_RUN", "DOCUMENTS", "DOCUMENTS_STORE"];

    private static readonly object Pack = new
    {
        pack = "AI3BASE", kind = "BASE",
        rows = new object[]
        {
            new
            {
                table = Calendars, code = "CAL", @class = "OPERATIONAL", regulated = false, tailorable = true, tailor = new { columns = new[] { "time_zone" }, from = new[] { "timeZone" } },
                values = new { calendar_code = "CAL", working_days = new[] { "MON", "TUE", "WED", "THU", "FRI" }, hours = "08:00-17:00", holiday_calendar = (string?)null, time_zone = (string?)null },
            },
            // Marked tailorable and named, but regulated: never tailored (AIA-082).
            new
            {
                table = "finance.cfg_tax_codes_on_financial_transactions", code = "VAT", @class = "FINANCE", regulated = true, tailorable = true,
                tailor = new { columns = new[] { "rate" }, from = new[] { "country" } }, values = new { rate = "15" },
            },
            new { table = Accounts, code = "ACC-1100", @class = "FINANCE", values = new { account_code = "1100", name = "Premium debtors", type = "ASSET", status = "ACTIVE", gl_account = "GL 1100" } },
        },
    };

    /// <summary>The tenant's document for these tests: as the published AI-3 sample, with the pack's own account too, to show a mapped row never replaces it.</summary>
    private static byte[] TenantDocument() => SampleDocuments.Pdf(
        ["Chart of accounts", "Account code\tAccount name\tType\tGL code", "4100\tPremium income\tINCOME\tGL 4100", "1100\tPremium debtors\tASSET\tGL 1100"],
        ["Approval matrix", "Claims managers approve claim payments up to ZAR 250,000.", "Finance managers approve premium refunds up to ZAR 50,000."],
        ["Role list", "Claims Officer", "Underwriter", "Compliance Officer"]);

    private sealed record World(Guid Tenant, string Code, InsysApiFactory Host, StubAiModelProvider Stub, HttpClient Wizard, Func<string, string[], Task<HttpClient>> User);

    private async Task Platform()
    {
        var host = await fixture.Host();
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "sa-mgr", "ASSIST_OVERLAY_MANAGE");
        await fixture.Database.UserWithFunctions(DatabaseFixture.Operator, "sa-lead", "ASSIST_OVERLAY_APPROVE");
        var manager = host.ClientFor(DatabaseFixture.Operator, "sa-mgr");
        var loaded = await manager.Send(HttpMethod.Post, "/api/v1/assist/overlays", Pack, idempotent: true);
        loaded.StatusCode.ShouldBe(HttpStatusCode.Created, await loaded.Content.ReadAsStringAsync());
        var view = await loaded.Json();
        var id = view.GetProperty("id").GetGuid();
        var reviewed = await manager.Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/review",
            new { partner = "Sample Partners (sample)", reviewerName = "C. Reviewer (sample)", reviewedOn = "2026-11-14", reviewDocumentId = Guid.CreateVersion7() }, ifMatch: loaded.ETag());
        reviewed.StatusCode.ShouldBe(HttpStatusCode.OK, await reviewed.Content.ReadAsStringAsync());
        (await host.ClientFor(DatabaseFixture.Operator, "sa-lead").Send(HttpMethod.Post, $"/api/v1/assist/overlays/{id}/approve",
            new { contentHash = view.GetProperty("contentHash").GetString() }, ifMatch: reviewed.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<World> Ready(string subject, bool aiEntitled = true)
    {
        await Platform();
        var tenant = Guid.CreateVersion7();
        var code = "SA" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await fixture.Database.SeedTenant(tenant, code);
        var host = await fixture.Host();
        if (aiEntitled)
        {
            // The seeded tenant has every feature but AI_ASSIST, which only the platform grants.
            await fixture.Database.ExecuteAsOwner("""
                INSERT INTO tenant.cfg_feature_entitlements (tenant_id, code, version, effective_from, record_status, created_by, change_reason, tenant, feature, enabled)
                VALUES (NULL, @code, 1, DATE '2026-01-01', 'EFFECTIVE', 'seed', 'test seed', @tenant, 'AI_ASSIST', true)
                """, ("code", $"{code}:AI_ASSIST"), ("tenant", code));
            await fixture.Database.UserWithFunctions(tenant, "sa-admin", "ASSIST_SETTINGS_MANAGE");
            (await host.ClientFor(tenant, "sa-admin").Send(HttpMethod.Put, "/api/v1/assist/settings",
                new { jurisdiction = "ZA", inferenceRegion = "GLOBAL", legalBasis = "POPIA s72, binding agreement (sample)", explanationsEnabled = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "SETUP_DOC", TestClock.Today.AddDays(-30), ("category_code", "SETUP_DOC"),
            ("label", "Setup document"), ("classification", "CONFIDENTIAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
        await fixture.Database.UserWithFunctions(tenant, subject, WizardFunctions);
        // The settings are a live tenant's screen: made first, then the tenant is onboarding, as the wizard needs (AIA-086).
        await fixture.Database.ExecuteAsOwner("UPDATE tenant.tenant SET status = 'ONBOARDING' WHERE id = @t", ("t", tenant));
        var stub = (StubAiModelProvider)host.Services.GetRequiredService<IAiModelProvider>();
        stub.Unavailable = false;
        stub.Region = "us";
        stub.Reply = _ => "{}";
        return new World(tenant, code, host, stub, host.ClientFor(tenant, subject), async (s, functions) =>
        {
            await fixture.Database.UserWithFunctions(tenant, s, functions);
            return host.ClientFor(tenant, s);
        });
    }

    private static async Task<(Guid Id, string ETag)> Start(World w, string timeZone = "Africa/Johannesburg")
    {
        var started = await w.Wizard.Send(HttpMethod.Post, Setups, new { country = "ZA", lines = new[] { "LIFE" }, currencies = new[] { "ZAR" }, timeZone }, idempotent: true);
        started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        return ((await started.Json()).GetProperty("id").GetGuid(), started.ETag());
    }

    private static async Task<Guid> Upload(World w, byte[] content, HttpClient? by = null, bool scan = true)
    {
        var stored = await (by ?? w.Wizard).Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "SETUP_DOC", businessRefType = "PARTY", businessRefId = Guid.CreateVersion7(), mimeType = "application/pdf", fileName = "tenant-setup",
            contentBase64 = Convert.ToBase64String(content),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync());
        if (scan)
        {
            await w.Host.ScanUploads();
        }

        return (await stored.Json()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> Attach(World w, Guid setup, string etag, Guid document, bool confirms = true, HttpClient? by = null) =>
        (by ?? w.Wizard).Send(HttpMethod.Post, $"{Setups}/{setup}/documents", new { documents = new[] { new { documentId = document, confirmsNoPersonalData = confirms } } },
            ifMatch: etag, idempotent: true);

    private static async Task<string> ETagOf(World w, Guid setup) => (await w.Wizard.GetAsync($"{Setups}/{setup}")).ETag();

    /// <summary>Queues a pass and runs the job; the setup and its newest pass after it.</summary>
    private static async Task<(JsonElement Setup, JsonElement Pass)> Pass(World w, Guid setup)
    {
        var queued = await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/ai-passes", ifMatch: await ETagOf(w, setup), idempotent: true);
        queued.StatusCode.ShouldBe(HttpStatusCode.Accepted, await queued.Content.ReadAsStringAsync());
        await w.Host.RunSetupAiPasses();
        var passes = await (await w.Wizard.GetAsync($"{Setups}/{setup}/ai-passes")).Json();
        return (await (await w.Wizard.GetAsync($"{Setups}/{setup}")).Json(), passes.GetProperty("passes")[0]);
    }

    private static JsonElement Value(JsonElement setup, string table, string code) =>
        setup.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("table").GetString() == table && v.GetProperty("code").GetString() == code);

    private static object Cite(int page, string quote) => new { document = 1, page, quote };

    private static string Reply(object output) => JsonSerializer.Serialize(output);

    /// <summary>A reply with one of each: kept and dropped tailoring and mapping, and a line the model couldn't map.</summary>
    private static string Mixed(string template) => Reply(new
    {
        tailored = new object[]
        {
            new { table = Calendars, code = "CAL", column = "time_zone", answer = "timeZone", value = "Africa/Johannesburg" },
            new { table = "finance.cfg_tax_codes_on_financial_transactions", code = "VAT", column = "rate", answer = "country", value = "ZA" },
        },
        mapped = new object[]
        {
            new { table = Accounts, value = new { account_code = "4100", name = "Premium income", type = "INCOME", gl_account = "GL 4100", status = "ACTIVE" }, citation = Cite(1, "4100 Premium income INCOME GL 4100") },
            new { table = Accounts, value = new { account_code = "1100", name = "Premium debtors", type = "ASSET", gl_account = "GL 1100", status = "ACTIVE" }, citation = Cite(1, "1100 Premium debtors ASSET GL 1100") },
            new { table = Authorities, value = new { role_code = "CLAIMS_MANAGER", transaction_type = "CLAIM_PAYMENT", limit_value = "250000", currency = "ZAR", delegation_allowed = false },
                citation = Cite(2, "Claims managers approve claim payments up to ZAR 250,000.") },
            new { table = Authorities, value = new { role_code = "FINANCE_MANAGER", transaction_type = "PREMIUM_REFUND", limit_value = "500000", currency = "ZAR", delegation_allowed = false },
                citation = Cite(2, "Finance managers approve premium refunds up to ZAR 50,000.") },
            new { table = Roles, value = new { role_code = template, title = "Claims Officer" }, citation = Cite(3, "Role list Claims Officer") },
        },
        unmapped = new object[] { new { document = 1, page = 3, quote = "Compliance Officer", table = Roles } },
    });

    [Fact]
    public async Task APass_TailorsOnlyAMarkedRowFromItsAnswer_AndMapsCitedRowsBesideThePacks_EachUndecided_AIA082_AIA083_AIA084_AIA087_OQ186_OQ187_OQ188()
    {
        var w = await Ready("sa-wizard");
        var (setup, etag) = await Start(w);
        var document = await Upload(w, TenantDocument());
        var attached = await Attach(w, setup, etag, document);
        attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());
        var template = Insys.Identity.Api.RoleTemplates.All.First(t => t.Code.Contains("CLAIM", StringComparison.Ordinal)).Code;
        w.Stub.Reply = _ => Mixed(template);

        var (after, pass) = await Pass(w, setup);

        pass.GetProperty("status").GetString().ShouldBe("DRAFTED", pass.ToString());
        (pass.GetProperty("tailored").GetInt32(), pass.GetProperty("mapped").GetInt32()).ShouldBe((1, 3), pass.ToString());
        // AIA-084: Tailored cites its answer; the pack row keeps its code and source, and only the named column changed.
        var calendar = Value(after, Calendars, "CAL");
        calendar.GetProperty("flag").GetString().ShouldBe("TAILORED");
        calendar.GetProperty("citedAnswer").GetString().ShouldBe("timeZone");
        calendar.GetProperty("citedAnswerValue").GetString().ShouldBe("Africa/Johannesburg");
        calendar.GetProperty("values").GetProperty("time_zone").GetString().ShouldBe("Africa/Johannesburg");
        calendar.GetProperty("values").GetProperty("hours").GetString().ShouldBe("08:00-17:00");
        // AIA-082: a regulated value is never tailored, even marked.
        Value(after, "finance.cfg_tax_codes_on_financial_transactions", "VAT").GetProperty("flag").GetString().ShouldBe("PACK");
        // AIA-083, OQ-186 (a): the tenant's own cited facts, beside the pack's (OQ-188), not regulated.
        var account = Value(after, Accounts, "ACC-4100");
        account.GetProperty("flag").GetString().ShouldBe("MAPPED");
        account.GetProperty("regulated").GetBoolean().ShouldBeFalse();
        account.GetProperty("citationDocumentId").GetGuid().ShouldBe(document);
        account.GetProperty("citationPage").GetInt32().ShouldBe(1);
        account.GetProperty("citationQuote").GetString().ShouldBe("4100 Premium income INCOME GL 4100");
        Value(after, Accounts, "ACC-1100").GetProperty("flag").GetString().ShouldBe("PACK", "the pack's own account is never replaced");
        Value(after, Authorities, "AUTH-CLAIM_PAYMENT-CLAIMS_MANAGER").GetProperty("values").GetProperty("limit_value").GetDecimal().ShouldBe(250000m);
        var role = Value(after, Roles, template);
        role.GetProperty("values").GetProperty("labels").GetProperty("en").GetString().ShouldBe("Claims Officer");
        role.GetProperty("values").TryGetProperty("permissions", out _).ShouldBeFalse("a title maps onto a template, never onto permissions (OQ-188)");
        // Ade, 19 November 2026: nothing takes effect until a person accepts it.
        after.GetProperty("values").EnumerateArray().Where(v => v.GetProperty("flag").GetString() is "TAILORED" or "MAPPED")
            .ShouldAllBe(v => v.GetProperty("decision").ValueKind == JsonValueKind.Null);
        // AIA-083: what wasn't mapped, and what INSYS dropped, with why.
        pass.GetProperty("unmapped").EnumerateArray().Select(u => u.GetProperty("reason").GetString()).Order()
            .ShouldBe(new[] { "CODE_IN_PACK", "NOT_IN_QUOTE", "NOT_MAPPED", "NOT_TAILORABLE" }.Order());
        // AIA-088: only the answers a tailorable row names were sent, and the regulated row wasn't offered.
        var sent = w.Stub.Sent.Last(s => s.Data.Contains("\"targets\"", StringComparison.Ordinal)).Data;
        sent.ShouldContain("Africa/Johannesburg");
        sent.ShouldNotContain("\"currencies\"");
        sent.ShouldNotContain("\"VAT\"");
    }

    [Fact]
    public async Task ATailoredOrMappedValue_BlocksSubmission_UntilAPersonDecidesIt_Ade19November()
    {
        var w = await Ready("sa-decider");
        var (setup, etag) = await Start(w);
        var document = await Upload(w, TenantDocument());
        (await Attach(w, setup, etag, document)).StatusCode.ShouldBe(HttpStatusCode.OK);
        w.Stub.Reply = _ => Mixed(Insys.Identity.Api.RoleTemplates.All[0].Code);
        var (after, _) = await Pass(w, setup);

        async Task<List<string>> Undecided() => [.. (await (await w.Wizard.GetAsync($"{Setups}/{setup}/completeness")).Json()).GetProperty("blockers").EnumerateArray()
            .Where(b => b.GetProperty("code").GetString() == "UNDECIDED").Select(b => $"{b.GetProperty("table").GetString()}:{b.GetProperty("rowCode").GetString()}")];

        var ai = after.GetProperty("values").EnumerateArray().Where(v => v.GetProperty("flag").GetString() is "TAILORED" or "MAPPED").ToList();
        ai.Count.ShouldBe(4);
        var undecided = await Undecided();
        ai.Select(v => $"{v.GetProperty("table").GetString()}:{v.GetProperty("code").GetString()}").ShouldAllBe(v => undecided.Contains(v));
        foreach (var value in ai)
        {
            (await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/items/{value.GetProperty("id").GetGuid()}/decision", new { decision = "ACCEPTED" },
                ifMatch: $"\"{value.GetProperty("rowVersion").GetInt64()}\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await Undecided()).ShouldNotContain(u => ai.Any(v => u == $"{v.GetProperty("table").GetString()}:{v.GetProperty("code").GetString()}"));
    }

    [Fact]
    public async Task ANewPass_ReplacesOnlyUndecidedValues_AndAcceptedOrRejectedValuesStayWithTheirAudit_Ade19November()
    {
        var w = await Ready("sa-again");
        var (setup, etag) = await Start(w);
        (await Attach(w, setup, etag, await Upload(w, TenantDocument()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var template = Insys.Identity.Api.RoleTemplates.All.First(t => t.Code.Contains("CLAIM", StringComparison.Ordinal)).Code;
        w.Stub.Reply = _ => Mixed(template);
        var (first, firstPass) = await Pass(w, setup);

        async Task Decide(JsonElement value, string decision) =>
            (await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/items/{value.GetProperty("id").GetGuid()}/decision", new { decision },
                ifMatch: $"\"{value.GetProperty("rowVersion").GetInt64()}\"")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Decide(Value(first, Calendars, "CAL"), "ACCEPTED");
        await Decide(Value(first, Accounts, "ACC-4100"), "ACCEPTED");
        await Decide(Value(first, Roles, template), "REJECTED");

        // The second pass proposes again for every row, with a different time zone answer value it can't use, and a new account.
        w.Stub.Reply = _ => Reply(new
        {
            tailored = new object[] { new { table = Calendars, code = "CAL", column = "time_zone", answer = "timeZone", value = "Africa/Johannesburg" } },
            mapped = new object[]
            {
                new { table = Accounts, value = new { account_code = "4100", name = "Premium income", type = "INCOME", gl_account = "GL 4100", status = "ACTIVE" }, citation = Cite(1, "4100 Premium income INCOME GL 4100") },
                new { table = Authorities, value = new { role_code = "CLAIMS_MANAGER", transaction_type = "CLAIM_PAYMENT", limit_value = "250000", currency = "ZAR", delegation_allowed = false },
                    citation = Cite(2, "Claims managers approve claim payments up to ZAR 250,000.") },
                new { table = Roles, value = new { role_code = template, title = "Claims Officer" }, citation = Cite(3, "Role list Claims Officer") },
            },
        });
        var (second, secondPass) = await Pass(w, setup);
        var pass1 = firstPass.GetProperty("id").GetGuid();
        var pass2 = secondPass.GetProperty("id").GetGuid();
        secondPass.GetProperty("status").GetString().ShouldBe("DRAFTED", secondPass.ToString());

        // Decided values stay as decided, from the pass that made them.
        foreach (var (table, code, decision) in new[] { (Calendars, "CAL", "ACCEPTED"), (Accounts, "ACC-4100", "ACCEPTED"), (Roles, template, "REJECTED") })
        {
            var kept = Value(second, table, code);
            (kept.GetProperty("decision").GetString(), kept.GetProperty("aiPassId").GetGuid(), kept.GetProperty("inDraft").GetBoolean()).ShouldBe((decision, pass1, true), $"{table}:{code}");
        }

        // The undecided value is replaced by the new pass's.
        var replaced = Value(second, Authorities, "AUTH-CLAIM_PAYMENT-CLAIMS_MANAGER");
        (replaced.GetProperty("aiPassId").GetGuid(), replaced.GetProperty("decision").ValueKind).ShouldBe((pass2, JsonValueKind.Null));

        await w.Host.DrainOutbox();
        var decided = new[] { Value(second, Calendars, "CAL"), Value(second, Accounts, "ACC-4100"), Value(second, Roles, template) }
            .Select(v => v.GetProperty("id").GetGuid().ToString()).ToArray();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(DISTINCT target_id) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'DecideSetupItem' AND outcome = 'SUCCESS' AND target_id = ANY(@ids)
            """, ("t", w.Tenant), ("ids", decided))).ShouldBe(3L, "each decision's audit stays");
    }

    [Fact]
    public async Task AttachingADocument_NeedsItsUploadersConfirmation_AndAScannedUpload_D16()
    {
        var w = await Ready("sa-attach");
        var (setup, etag) = await Start(w);
        var other = await w.User("sa-other", WizardFunctions);

        var unconfirmed = await Attach(w, setup, etag, await Upload(w, TenantDocument()), confirms: false);
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unconfirmed.Code()).ShouldBe("ASSIST_CONFIRMATION_REQUIRED");

        var someoneElses = await Attach(w, setup, etag, await Upload(w, SampleDocuments.Ai3Sample(), by: other));
        someoneElses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await someoneElses.Code()).ShouldBe("ASSIST_NOT_UPLOADER");

        var held = await Attach(w, setup, etag, await Upload(w, SampleDocuments.Ai3Sample(), scan: false));
        held.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await held.Code()).ShouldBe("DOCUMENT_QUARANTINED");

        var document = await Upload(w, TenantDocument());
        (await Attach(w, setup, "\"999\"", document)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var attached = await Attach(w, setup, etag, document);
        attached.StatusCode.ShouldBe(HttpStatusCode.OK, await attached.Content.ReadAsStringAsync());
        var listed = (await attached.Json()).GetProperty("documents").EnumerateArray().ShouldHaveSingleItem();
        listed.GetProperty("confirmedBy").GetString().ShouldNotBeNullOrEmpty();
        await w.Host.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ConfirmNoPersonalData'", ("t", w.Tenant)))
            .ShouldBe(1L, "the confirmation is audited (IAM-006)");

        var removed = await w.Wizard.Send(HttpMethod.Delete, $"{Setups}/{setup}/documents/{document}", ifMatch: attached.ETag());
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.Json()).GetProperty("documents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ADocumentHoldingAnIdentifier_RefusesThePass_BeforeAnythingIsSent_D16()
    {
        var w = await Ready("sa-ident");
        var (setup, etag) = await Start(w);
        var document = await Upload(w, SampleDocuments.Pdf(["Role list", "Claims Officer: send queries to claims.officer@example.com"]));
        (await Attach(w, setup, etag, document)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var calls = w.Stub.Sent.Count;

        var (_, pass) = await Pass(w, setup);

        pass.GetProperty("status").GetString().ShouldBe("REFUSED");
        pass.GetProperty("outcomeCode").GetString().ShouldBe("ASSIST_DOCUMENT_IDENTIFIERS");
        w.Stub.Sent.Count.ShouldBe(calls, "nothing reached a model");
        var read = (await (await w.Wizard.GetAsync($"{Setups}/{setup}/documents")).Json()).GetProperty("documents")[0];
        read.GetProperty("identifiers")[0].GetProperty("kind").GetString().ShouldBe("EMAIL");
        read.GetProperty("identifiers")[0].GetProperty("page").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task WithoutTheEntitlement_APassIsRefused_AndThePackOnlyDraftStillWorks_AIA080_AIA090()
    {
        var w = await Ready("sa-noai", aiEntitled: false);
        var (setup, etag) = await Start(w);
        var refused = await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/ai-passes", ifMatch: etag, idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Code()).ShouldBe("FEATURE_NOT_ENTITLED");
        var draft = await (await w.Wizard.GetAsync($"{Setups}/{setup}")).Json();
        Value(draft, Calendars, "CAL").GetProperty("flag").GetString().ShouldBe("PACK");
    }

    [Fact]
    public async Task OnePassAtATime_AndAPassWhoseAnswersChanged_RecordsNothing_AIA081()
    {
        var w = await Ready("sa-once");
        var (setup, etag) = await Start(w);
        w.Stub.Reply = _ => Mixed(Insys.Identity.Api.RoleTemplates.All[0].Code);
        (await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/ai-passes", ifMatch: etag, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var second = await w.Wizard.Send(HttpMethod.Post, $"{Setups}/{setup}/ai-passes", ifMatch: await ETagOf(w, setup), idempotent: true);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Code()).ShouldBe("ASSIST_SETUP_PASS_OPEN");

        (await w.Wizard.Send(HttpMethod.Put, $"{Setups}/{setup}/answers", new { country = "ZA", lines = new[] { "LIFE" }, timeZone = "Africa/Maputo" },
            ifMatch: await ETagOf(w, setup))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await w.Host.RunSetupAiPasses();

        var pass = (await (await w.Wizard.GetAsync($"{Setups}/{setup}/ai-passes")).Json()).GetProperty("passes")[0];
        pass.GetProperty("status").GetString().ShouldBe("FAILED");
        pass.GetProperty("outcomeCode").GetString().ShouldBe("ASSIST_SETUP_CHANGED");
        Value(await (await w.Wizard.GetAsync($"{Setups}/{setup}")).Json(), Calendars, "CAL").GetProperty("flag").GetString().ShouldBe("PACK");
    }

    [Fact]
    public async Task NewAnswers_KeepATailoredValueOnlyWhileItsAnswerHoldsIt_AndKeepMappedRows_AIA081()
    {
        var w = await Ready("sa-reanswer");
        var (setup, etag) = await Start(w);
        var document = await Upload(w, TenantDocument());
        (await Attach(w, setup, etag, document)).StatusCode.ShouldBe(HttpStatusCode.OK);
        w.Stub.Reply = _ => Mixed(Insys.Identity.Api.RoleTemplates.All[0].Code);
        await Pass(w, setup);

        async Task<JsonElement> Answer(string timeZone, IEnumerable<string>? currencies = null)
        {
            var answered = await w.Wizard.Send(HttpMethod.Put, $"{Setups}/{setup}/answers",
                new { country = "ZA", lines = new[] { "LIFE" }, currencies = currencies ?? ["ZAR"], timeZone }, ifMatch: await ETagOf(w, setup));
            answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
            return await answered.Json();
        }

        var same = await Answer("Africa/Johannesburg", ["ZAR", "USD"]);
        Value(same, Calendars, "CAL").GetProperty("flag").GetString().ShouldBe("TAILORED", "its answer still holds the value it cited");
        var changed = await Answer("Africa/Maputo");
        Value(changed, Calendars, "CAL").GetProperty("flag").GetString().ShouldBe("PACK", "its answer no longer holds it: the pack's value again, undecided");
        Value(changed, Calendars, "CAL").GetProperty("values").GetProperty("time_zone").ValueKind.ShouldBe(JsonValueKind.Null);
        var mapped = Value(changed, Accounts, "ACC-4100");
        (mapped.GetProperty("flag").GetString(), mapped.GetProperty("inDraft").GetBoolean()).ShouldBe(("MAPPED", true), "a document's row isn't the answers' to remove");
    }

    [Fact]
    public async Task ASetupsDocumentsAndPasses_AreItsTenantsOnly_RLS()
    {
        var w = await Ready("sa-own");
        var (setup, etag) = await Start(w);
        (await Attach(w, setup, etag, await Upload(w, TenantDocument()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await Ready("sa-else");
        (await other.Wizard.GetAsync($"{Setups}/{setup}/documents")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Wizard.GetAsync($"{Setups}/{setup}/ai-passes")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var connection = new Npgsql.NpgsqlConnection(fixture.Database.ConnectionFor("insys_assist"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new Npgsql.NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", other.Tenant.ToString());
            await set.ExecuteScalarAsync();
        }

        await using var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM assist.setup_document", connection, transaction);
        ((long)(await count.ExecuteScalarAsync())!).ShouldBe(0L, "row-level security: another tenant's session sees none of this setup's documents");
    }
}
