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
using System.Net.Http.Headers;
using System.Text.Json;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.General.Contracts;
using Insys.Party.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Languages (docs/specs/addenda/localisation.md, OQ-068): the request language from Accept-Language and the tenant's
/// languages, each fallback step (request language, tenant default, English, the label column), localised ProblemDetails
/// with an unchanged code, Content-Language, the party's preferred language, and documents in the party's language.
/// Tenant A supports English (default), Afrikaans, isiZulu and isiXhosa; tenant B supports Afrikaans (default) and English.
/// </summary>
public sealed class LocalisationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid A = DatabaseFixture.TenantA;
    private static readonly Guid B = DatabaseFixture.TenantB;
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);

    private static readonly string[] Permissions =
    [
        StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate,
    ];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        _factory = await fixture.Host();
        await db.UserWithPermissions(A, "lang-a", Permissions);
        await db.UserWithPermissions(B, "lang-b", Permissions);
        foreach (var (code, name) in new[] { ("en", "English"), ("af", "Afrikaans"), ("zu", "isiZulu"), ("xh", "isiXhosa") })
        {
            await db.SeedGlobalStatic("refdata.ref_languages", "ACTIVE", code, From, ("name", name));
        }

        await db.SeedStatic(A, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE", From, ("tenant", "A"), ("base_currency", "ZAR"),
            ("languages", new[] { "en", "af", "zu", "xh" }), ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        await db.SeedStatic(B, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE", From, ("tenant", "B"), ("base_currency", "ZAR"),
            ("languages", new[] { "en", "af" }), ("default_language", "af"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));

        // Static data: one frequency translated into three languages, one with English only, one with no labels at all.
        foreach (var tenant in new[] { A, B })
        {
            await db.SeedStatic(tenant, "billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", From, ("label", "Monthly"), ("periods_per_year", 12),
                ("labels", Labels(("en", "Monthly"), ("af", "Maandeliks"), ("zu", "Nyanga zonke"))));
            await db.SeedStatic(tenant, "billing.ref_billing_frequencies", "ACTIVE", "ANNUAL", From, ("label", "Annual"), ("periods_per_year", 1),
                ("labels", Labels(("en", "Yearly"))));
            await db.SeedStatic(tenant, "billing.ref_billing_frequencies", "ACTIVE", "QUARTERLY", From, ("label", "Quarterly"), ("periods_per_year", 4));
        }

        // The message catalogue: Afrikaans for every tenant (global), and isiZulu with a detail for tenant A only.
        await db.SeedGlobalStatic("channel.ref_ui_label_and_translation_bundles", "ACTIVE", "NOT_FOUND-af", From,
            ("label_key", "error.resource.not_found"), ("language", "af"), ("text", "Die hulpbron is nie gevind nie."));
        await db.SeedStatic(A, "channel.ref_ui_label_and_translation_bundles", "ACTIVE", "NOT_FOUND-zu", From,
            ("label_key", "error.resource.not_found"), ("language", "zu"), ("text", "Okufunwayo akutholakalanga."));
        await db.SeedStatic(A, "channel.ref_ui_label_and_translation_bundles", "ACTIVE", "NOT_FOUND-zu-detail", From,
            ("label_key", "error.resource.not_found.detail"), ("language", "zu"), ("text", "Akukho rekhodi elinale khodi."));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task StaticDataLabel_IsInTheRequestedLanguage_WhenTheTenantSupportsIt()
    {
        var response = await Get(A, "lang-a", "/api/v1/billing/static/billing-frequencies/MONTHLY", "zu-ZA, en;q=0.5");

        var record = await response.Json();
        (record.GetProperty("displayLabel").GetString(), record.GetProperty("displayLabelLanguage").GetString()).ShouldBe(("Nyanga zonke", "zu"));
        response.Content.Headers.ContentLanguage.ShouldBe(["zu"]);
        record.GetProperty("labels").GetProperty("af").GetString().ShouldBe("Maandeliks");
    }

    [Fact]
    public async Task UnsupportedLanguage_FallsBackToTheTenantDefault()
    {
        // isiZulu is not one of tenant B's languages, nor is French: both resolve to Afrikaans, B's default.
        foreach (var header in new[] { "zu", "fr-FR", "" })
        {
            var response = await Get(B, "lang-b", "/api/v1/billing/static/billing-frequencies/MONTHLY", header);
            (await response.Json()).GetProperty("displayLabel").GetString().ShouldBe("Maandeliks", header);
            response.Content.Headers.ContentLanguage.ShouldBe(["af"], header);
        }
    }

    [Fact]
    public async Task MissingText_FallsBackToTheTenantDefault_ThenEnglish_ThenTheLabelColumn()
    {
        // A supports isiXhosa, but the record has no isiXhosa text: A's default, English, is used.
        var xhosa = await (await Get(A, "lang-a", "/api/v1/billing/static/billing-frequencies/MONTHLY", "xh")).Json();
        (xhosa.GetProperty("displayLabel").GetString(), xhosa.GetProperty("displayLabelLanguage").GetString()).ShouldBe(("Monthly", "en"));

        // B's default is Afrikaans; a record with English only falls back to English.
        var annual = await (await Get(B, "lang-b", "/api/v1/billing/static/billing-frequencies/ANNUAL", "af")).Json();
        (annual.GetProperty("displayLabel").GetString(), annual.GetProperty("displayLabelLanguage").GetString()).ShouldBe(("Yearly", "en"));

        // No labels at all: the table's own label column, as English.
        var quarterly = await (await Get(B, "lang-b", "/api/v1/billing/static/billing-frequencies/QUARTERLY", "af")).Json();
        (quarterly.GetProperty("displayLabel").GetString(), quarterly.GetProperty("displayLabelLanguage").GetString()).ShouldBe(("Quarterly", "en"));

        // Lists are localised too.
        var page = await (await Get(A, "lang-a", "/api/v1/billing/static/billing-frequencies", "af")).Json();
        page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "MONTHLY").GetProperty("displayLabel").GetString()
            .ShouldBe("Maandeliks");
    }

    [Fact]
    public async Task ProblemDetails_AreLocalisedFromTheCatalogue_AndTheCodeNeverChanges()
    {
        // Afrikaans title (a global row); no Afrikaans detail, so the English detail is kept and marked.
        var afrikaans = await Get(B, "lang-b", "/api/v1/billing/static/billing-frequencies/WEEKLY", "af");
        afrikaans.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await afrikaans.Json();
        problem.GetProperty("code").GetString().ShouldBe("NOT_FOUND");
        problem.GetProperty("title").GetString().ShouldBe("Die hulpbron is nie gevind nie.");
        problem.GetProperty("detailLanguage").GetString().ShouldBe("en");
        afrikaans.Content.Headers.ContentLanguage.ShouldBe(["af"]);

        // Tenant A's own isiZulu row has a detail too.
        var zulu = await (await Get(A, "lang-a", "/api/v1/billing/static/billing-frequencies/WEEKLY", "zu")).Json();
        (zulu.GetProperty("code").GetString(), zulu.GetProperty("title").GetString(), zulu.GetProperty("detail").GetString())
            .ShouldBe(("NOT_FOUND", "Okufunwayo akutholakalanga.", "Akukho rekhodi elinale khodi."));
        zulu.TryGetProperty("detailLanguage", out _).ShouldBeFalse();

        // Tenant B has no isiZulu row and does not support isiZulu: Afrikaans.
        (await (await Get(B, "lang-b", "/api/v1/billing/static/billing-frequencies/WEEKLY", "zu")).Json()).GetProperty("title").GetString()
            .ShouldBe("Die hulpbron is nie gevind nie.");

        // isiXhosa has no text in tenant A: its default, English, which is the built-in title.
        var xhosa = await Get(A, "lang-a", "/api/v1/billing/static/billing-frequencies/WEEKLY", "xh");
        (await xhosa.Json()).GetProperty("code").GetString().ShouldBe("NOT_FOUND");
        xhosa.Content.Headers.ContentLanguage.ShouldBe(["en"]);
    }

    /// <summary>Catalogue texts are cached; activating a translation drops the cache, so the next request has it (no TTL wait).</summary>
    [Fact]
    public async Task ActivatedTranslation_ReplacesTheCachedText()
    {
        // VALIDATION_FAILED in isiXhosa, which no other test here reads, so the new row affects no other test.
        var invalid = new { code = "BIWEEKLY", label = "Bi-weekly", periodsPerYear = 26, changeReason = "test", labels = new { english = "Bi-weekly" } };
        async Task<string?> Title() =>
            (await (await Client(A, "lang-a", "xh").Send(HttpMethod.Post, "/api/v1/billing/static/billing-frequencies", invalid, idempotent: true)).Json())
            .GetProperty("title").GetString();

        (await Title()).ShouldNotBe("Isicelo asivumelekanga.");
        await fixture.Database.SeedStatic(A, "channel.ref_ui_label_and_translation_bundles", "ACTIVE", "VALIDATION_FAILED-xh", From,
            ("label_key", "error.request.validation_failed"), ("language", "xh"), ("text", "Isicelo asivumelekanga."));
        (await Title()).ShouldNotBe("Isicelo asivumelekanga.", "still cached");

        var record = new StaticRecordRef("channel", "ref_ui_label_and_translation_bundles", "ref_ui_label_and_translation_bundles", "integration",
            "ui-label-and-translation-bundles", Guid.CreateVersion7(), "VALIDATION_FAILED-xh", 1, false);
        await _factory.Services.GetRequiredService<IEventBus>().Publish(new EventEnvelope(Guid.CreateVersion7(), A, "sdm.StaticDataVersionActivated", 1,
            DateTimeOffset.UtcNow, "StaticRecord", record.RecordId, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(new StaticDataVersionActivated(null, record, From, null), ModuleDbContext.EventJson)), CancellationToken.None);

        (await Title()).ShouldBe("Isicelo asivumelekanga.");
    }

    [Fact]
    public async Task RequestWithoutATenant_IsAnsweredInEnglish()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("af");
        var response = await client.GetAsync("/api/v1/billing/static/billing-frequencies/MONTHLY");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentLanguage.ShouldBe(["en"]);
    }

    /// <summary>The default language is an explicit setting that must be one of the tenant's languages (V0022, addendum §1).</summary>
    [Fact]
    public async Task DefaultLanguage_MustBeOneOfTheTenantsLanguages()
    {
        var response = await Client(A, "lang-a", null).Send(HttpMethod.Post, "/api/v1/tenancy/static/locale-defaults", new
        {
            code = "LOCALE-NEXT", tenant = "A", baseCurrency = "ZAR", languages = new[] { "en", "af" }, defaultLanguage = "zu",
            timeZone = "Africa/Johannesburg", country = "ZA", changeReason = "test",
        }, idempotent: true);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Json()).GetProperty("errors").GetProperty("defaultLanguage").EnumerateArray().Single().GetString().ShouldBe("Must be one of: en, af.");
        await Should.ThrowAsync<Npgsql.PostgresException>(() => fixture.Database.SeedStatic(A, "tenant.cfg_locale_defaults", "DRAFT", "LOCALE-BAD", From.AddDays(400),
            ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "af"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA")));
    }

    [Fact]
    public async Task Labels_HoldDisplayTextOnly_ByLanguageCode()
    {
        var client = Client(A, "lang-a", null);
        var response = await client.Send(HttpMethod.Post, "/api/v1/billing/static/billing-frequencies",
            new { code = "WEEKLY", label = "Weekly", periodsPerYear = 52, changeReason = "test", labels = new { english = "Weekly", af = 7 } }, idempotent: true);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var errors = (await response.Json()).GetProperty("errors").GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToList();
        errors.ShouldContain(e => e!.Contains("english", StringComparison.Ordinal));
        errors.ShouldContain(e => e!.Contains("af label", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreferredLanguage_IsSetAndChangedThroughTheApi_FromTheLanguageRegister()
    {
        var client = Client(A, "lang-a", null);
        var party = await client.RegisterPerson("Dlamini");

        (await SetLanguage(client, party, "zu")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await client.GetAsync($"/api/v1/party/parties/{party}")).Json()).GetProperty("individual").GetProperty("preferredLanguage").GetString().ShouldBe("zu");
        (await SetLanguage(client, party, "xh")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.Database.ScalarAsOwner<string>("SELECT preferred_language FROM party.individual WHERE party_id = @p", ("p", party))).ShouldBe("xh");

        // Not in the register, or not a code at all.
        (await (await SetLanguage(client, party, "qq")).Code()).ShouldBe("VALIDATION_FAILED");
        (await (await SetLanguage(client, party, "ZU")).Code()).ShouldBe("VALIDATION_FAILED");
        (await SetLanguage(client, party, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.Database.ScalarAsOwner<string>("SELECT preferred_language FROM party.individual WHERE party_id = @p", ("p", party))).ShouldBeNull();
    }

    /// <summary>A non-English path end to end: a renewal invitation for an isiZulu-speaking policyholder is generated from the isiZulu template.</summary>
    [Fact]
    public async Task Document_IsGeneratedInThePartysLanguage_ThenTheTenantDefault_ThenEnglish_AndRecordsIt()
    {
        foreach (var tenant in new[] { A, B })
        {
            await fixture.Database.SeedStatic(tenant, "document.ref_document_categories", "ACTIVE", "RENEWAL_NOTICE", From, ("category_code", "RENEWAL_NOTICE"),
                ("label", "Renewal notice"), ("classification", "PERSONAL"), ("domain", "ALL"), ("allowed_roles", Array.Empty<string>()));
            foreach (var language in tenant == A ? new[] { "en", "zu" } : ["en"])
            {
                await fixture.Database.SeedStatic(tenant, "document.cfg_document_templates", "EFFECTIVE", $"TPL-REN-{language}", From, ("template_code", "TPL-REN"),
                    ("language", language), ("document_type", "RENEWAL_NOTICE"), ("body_ref", $"doc://templates/ren/{language}/v1"), ("merge_fields", new[] { "policy_number" }));
            }
        }

        var client = Client(A, "lang-a", null);
        var zulu = await client.RegisterPerson("Zulu");
        (await SetLanguage(client, zulu, "zu")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var xhosa = await client.RegisterPerson("Xhosa");
        (await SetLanguage(client, xhosa, "xh")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // isiZulu template; no isiXhosa template, so A's default (English); B's default is Afrikaans, which has none, so English.
        (await Invite(A, zulu)).ShouldBe("zu");
        (await Invite(A, xhosa)).ShouldBe("en");
        (await Invite(B, null)).ShouldBe("en");
    }

    private async Task<string?> Invite(Guid tenant, Guid? party)
    {
        var policy = Guid.CreateVersion7();
        var invited = new GeneralRenewalInvited(policy, $"GP-{policy.ToString("N")[^6..]}", "GEN-MOTOR", 1, TestClock.Today.AddDays(30), "TPL-REN", party);
        await _factory.Services.GetRequiredService<IEventBus>().Publish(new EventEnvelope(Guid.CreateVersion7(), tenant, "general.GeneralRenewalInvited", 1,
            DateTimeOffset.UtcNow, "GeneralPolicy", policy, Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(invited, ModuleDbContext.EventJson)), CancellationToken.None);
        return await fixture.Database.ScalarAsOwner<string>("""
            SELECT g.language FROM document.generation_request g JOIN document.document d ON d.id = g.document_id
            WHERE d.business_ref_id = @p AND d.category_code = 'RENEWAL_NOTICE'
            """, ("p", policy));
    }

    private async Task<HttpResponseMessage> SetLanguage(HttpClient client, Guid party, string? language)
    {
        var etag = (await client.GetAsync($"/api/v1/party/parties/{party}")).ETag();
        return await client.Send(HttpMethod.Put, $"/api/v1/party/parties/{party}/preferred-language", new { language }, ifMatch: etag);
    }

    private Task<HttpResponseMessage> Get(Guid tenant, string subject, string url, string acceptLanguage) => Client(tenant, subject, acceptLanguage).GetAsync(url);

    private HttpClient Client(Guid tenant, string subject, string? acceptLanguage)
    {
        var client = _factory.ClientFor(tenant, subject);
        if (!string.IsNullOrEmpty(acceptLanguage))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return client;
    }

    private static JsonElement Labels(params (string Language, string Text)[] labels) =>
        JsonSerializer.SerializeToElement(labels.ToDictionary(l => l.Language, l => l.Text));
}
