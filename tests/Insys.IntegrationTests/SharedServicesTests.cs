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
using System.Text;
using Insys.Documents.Contracts;
using Insys.Party.Contracts;
using Insys.Rating.Contracts;
using Insys.Rules.Contracts;

namespace Insys.IntegrationTests;

/// <summary>Rules, Rating, Party and Documents through the API, against the real migrations as the module roles.</summary>
public sealed class SharedServicesTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid TenantA = DatabaseFixture.TenantA;
    private static readonly string[] All =
    [
        RulesPermissions.DecisionCreate, RulesPermissions.DecisionRead, RatingPermissions.CalculationCreate, RatingPermissions.CalculationRead,
        RatingPermissions.Simulate, PartyPermissions.PartyRead, PartyPermissions.PartyCreate, PartyPermissions.PartyUpdate, PartyPermissions.PartyMerge,
        PartyPermissions.ConsentRead, PartyPermissions.ConsentUpdate, DocumentPermissions.DocumentRead, DocumentPermissions.DocumentCreate,
        DocumentPermissions.DocumentDeliver, DocumentPermissions.DocumentDispose, DocumentPermissions.LegalHoldManage,
    ];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(TenantA, "shared-user", All);
        var assessor = await fixture.Database.UserWithPermissions(TenantA, "claims-assessor", All);
        await Static("document.ref_document_categories", "MEDICAL_EVIDENCE", "category_code, label, classification, domain, allowed_roles",
            "'MEDICAL_EVIDENCE', 'Medical evidence', 'HIGHLY_SENSITIVE', 'HEALTH', ARRAY['CLAIMS_ASSESSOR']", reference: true);
        await Static("iam.cfg_roles_and_permission_sets", "CLAIMS_ASSESSOR", "role_code", "'CLAIMS_ASSESSOR'");
        await fixture.Database.GrantFunctionsWithin(TenantA, "CLAIMS_ASSESSOR", TestClock.Today.AddDays(-1), "document.read");
        if (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE user_id = @u AND role_code = 'CLAIMS_ASSESSOR'", ("u", assessor)) == 0)
        {
            await fixture.Database.AssignRole(TenantA, assessor, "CLAIMS_ASSESSOR");
        }

        await Static("rules.cfg_eligibility_rule_sets", "ADULT", "ruleset_code, conditions, outcome", "'ELIG-TERM', 'age_next >= 18 AND age_next <= 65', 'ELIGIBLE'");
        await Static("rules.cfg_eligibility_rule_sets", "OLDER", "ruleset_code, conditions, outcome", "'ELIG-TERM', 'age_next > 60', 'REFER'");
        await Static("rating.cfg_rating_factor_definitions", "AGE", "factor_code, data_type, source_attribute, banding",
            """'AGE', 'INTEGER', 'life.age', '["18-39","40-65"]'""");
        await Static("rating.cfg_rating_tables", "LIFE-BASE", "table_code, dimensions, values, interpolation",
            """'LIFE-BASE', ARRAY['AGE'], '[{"key":["18-39"],"value":0.002},{"key":["40-65"],"value":0.005}]', 'NONE'""");
        await Static("rating.cfg_premium_parameters", "TERM-BASE", "parameter_code, product_version, value, unit", "'LIFE-BASE', 'LIF-TERM v1', 1, 'FACTOR'");
        await Static("rating.cfg_rounding_and_currency_precision_rules", "ZAR-2DP", "rule_code, currency, step, method, stage", "'ZAR-2DP', 'ZAR', 0.01, 'HALF_UP', 'FINAL_PREMIUM'");
        await Static("party.ref_identity_document_types", "ZA-NID", "country, doc_type, label, format_pattern", "'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$'",
            reference: true);
        await Static("party.cfg_consent_purposes", "MKT_EMAIL", "purpose_code, label, legal_basis, jurisdiction, default_value", "'MKT_EMAIL', 'Marketing', 'CONSENT', 'ZA', false");
        await Static("document.ref_document_categories", "POLICY_SCHEDULE", "category_code, label, classification, domain, allowed_roles",
            "'POLICY_SCHEDULE', 'Policy schedule', 'PERSONAL', 'ALL', ARRAY[]::text[]", reference: true);
        await Static("document.cfg_document_templates", "TPL-SCH", "template_code, language, document_type, body_ref, merge_fields",
            "'TPL-SCH', 'en', 'POLICY_SCHEDULE', 'doc://templates/sch/v1', ARRAY['policy.number','insured.name']");
        await Static("document.cfg_retention_schedules", "RET-SCH", "category, jurisdiction, period, trigger_event, disposal_method",
            "'POLICY_SCHEDULE', 'ZA', interval 'P5Y', 'CREATION', 'DELETE'");
        await Static("document.cfg_delivery_channel_settings", "EMAIL", "channel, provider, sender_identity", "'EMAIL', 'STUB', 'noreply@example.com'");
        await Static("document.ref_legal_hold_reason_codes", "LITIGATION", "label, authority_role", "'Pending litigation', 'COMPLIANCE_OFFICER'", reference: true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RuleDecision_IsRecordedWithItsVersionAndInputs_AndAFailedEvaluationRecordsNothing_LIF002()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var subject = Guid.CreateVersion7();
        var decided = await client.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Eligibility", code = "ELIG-TERM", subjectType = "APPLICATION", subjectId = subject, inputs = new { age_next = 62 } }, idempotent: true);
        decided.StatusCode.ShouldBe(HttpStatusCode.Created);
        var decision = await decided.Json();
        decision.GetProperty("outcome").GetString().ShouldBe("REFER");
        decision.GetProperty("rulesetVersion").GetInt32().ShouldBe(1);
        decision.GetProperty("firedRules").EnumerateArray().Select(r => r.GetString()).ShouldBe(["ADULT", "OLDER"]);

        var failed = await client.Send(HttpMethod.Post, "/api/v1/rules/decisions",
            new { kind = "Eligibility", code = "ELIG-TERM", subjectType = "APPLICATION", subjectId = subject, inputs = new { age = 62 } }, idempotent: true);
        (await failed.Code()).ShouldBe("EXPRESSION_EVALUATION_FAILED");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM rules.decision_log WHERE subject_id = @s", ("s", subject))).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<string>("SELECT inputs->>'age_next' FROM rules.decision_log WHERE subject_id = @s", ("s", subject))).ShouldBe("62");
    }

    /// <summary>An engineering case: the premium is worked from the engine's rules, not supplied by an actuary (docs/rating/test-cases.md).</summary>
    [Fact]
    [Trait("Category", "EngineeringCase")]
    public async Task PremiumCalculation_IsRecordedWithItsBreakdown_AndIsTenantScoped_PRD008()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var created = await client.Send(HttpMethod.Post, "/api/v1/rating/calculations", new
        {
            requestRef = "Q-1", sourceDomain = "LIFE", subjectType = "QUOTE", subjectId = Guid.CreateVersion7(), productCode = "LIF-TERM", productVersion = 1,
            currency = "ZAR", frequency = "MONTHLY", values = new { sum_assured = 1_000_000, life = new { age = 45 } },
        }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var calculation = await created.Json();
        calculation.GetProperty("premium").GetDecimal().ShouldBe(5000m);
        calculation.GetProperty("ratingVersion").GetInt32().ShouldBe(1);
        var id = calculation.GetProperty("id").GetGuid();

        var stored = await (await client.GetAsync($"/api/v1/rating/calculations/{id}")).Json();
        stored.GetProperty("lines").GetArrayLength().ShouldBe(3);
        stored.GetProperty("inputs").GetProperty("values").GetProperty("life").GetProperty("age").GetInt32().ShouldBe(45);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM rating.rating_result_line WHERE request_id = @id", ("id", id))).ShouldBe(3);

        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "shared-other", All);
        (await _factory.ClientFor(DatabaseFixture.TenantB, "shared-other").GetAsync($"/api/v1/rating/calculations/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var simulated = await client.Send(HttpMethod.Post, "/api/v1/rating/calculations/simulate", new
        {
            productCode = "LIF-TERM", productVersion = 1, currency = "ZAR", frequency = "MONTHLY", values = new { sum_assured = 1_000_000, life = new { age = 30 } },
        });
        (await simulated.Json()).GetProperty("premium").GetDecimal().ShouldBe(2000m);
    }

    [Fact]
    public async Task Identifier_IsEncrypted_UniqueInTheTenant_AndNeverReturned_PTY005()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var first = await Register(client, "Thandi", "Nkosi");
        var second = await Register(client, "Thandi", "Nkosi-Dlamini");

        (await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{first}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = "12345" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{first}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = "9001015800085" }, ifMatch: "\"1\""))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{second}/identifiers",
            new { docType = "NATIONAL_ID", country = "ZA", number = "9001015800085" }, ifMatch: "\"1\"");
        (await duplicate.Code()).ShouldBe("PARTY_IDENTIFIER_IN_USE");

        var encrypted = await fixture.Database.ScalarAsOwner<byte[]>("SELECT number_enc FROM party.party_identifier WHERE party_id = @p", ("p", first));
        Encoding.UTF8.GetString(encrypted!).ShouldNotContain("9001015800085");
        var party = await (await client.GetAsync($"/api/v1/party/parties/{first}")).Json();
        party.GetProperty("identifiers")[0].GetRawText().ShouldNotContain("9001015800085");
        party.GetProperty("identifiers")[0].GetProperty("docType").GetString().ShouldBe("NATIONAL_ID");

        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.outbox_event WHERE payload::text LIKE '%Nkosi%' OR payload::text LIKE '%9001015800085%'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task MergedParty_IsReadOnly_WithAnAuditableHistory_PTY004()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var survivor = await Register(client, "Sipho", "Dube");
        var duplicate = await Register(client, "Sipho", "Dube");
        (await client.Send(HttpMethod.Post, "/api/v1/party/merges", new { survivorId = survivor, mergedId = duplicate, reason = "Same person" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var merged = await (await client.GetAsync($"/api/v1/party/parties/{duplicate}")).Json();
        merged.GetProperty("status").GetString().ShouldBe("Merged");
        merged.GetProperty("mergedIntoId").GetGuid().ShouldBe(survivor);
        (await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{duplicate}/contact-points", new { channelType = "EMAIL", value = "s@example.com", isPrimary = true },
            ifMatch: "\"2\"")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.merge_history WHERE merged_id = @m AND survivor_id = @s", ("m", duplicate), ("s", survivor)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task ConsentLedger_AppendsAndTheLatestRecordIsCurrent_PTY005()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var party = await Register(client, "Lerato", "Molefe");
        var url = $"/api/v1/party/parties/{party}/consents";
        (await client.Send(HttpMethod.Post, $"{url}/grant", new { purposeCode = "MKT_EMAIL", captureChannel = "PORTAL", evidenceRef = "form-1" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.Send(HttpMethod.Post, $"{url}/withdraw", new { purposeCode = "MKT_EMAIL", captureChannel = "CALL_CENTRE" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.Send(HttpMethod.Post, $"{url}/grant", new { purposeCode = "UNKNOWN", captureChannel = "PORTAL" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var current = await (await client.GetAsync(url)).Json();
        current[0].GetProperty("granted").GetBoolean().ShouldBeFalse();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.consent_record WHERE party_id = @p", ("p", party))).ShouldBe(2);
    }

    [Fact]
    public async Task GeneratedDocument_RecordsItsVersions_IsDelivered_AndKeptUntilRetention_DOC002()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var generated = await client.Send(HttpMethod.Post, "/api/v1/documents/documents/generate", new
        {
            templateCode = "TPL-SCH", language = "en", categoryCode = "POLICY_SCHEDULE", businessRefType = "POLICY", businessRefId = Guid.CreateVersion7(),
            productVersionRef = "LIF-TERM v1", jurisdiction = "ZA", data = new Dictionary<string, object> { ["policy.number"] = "P-1", ["insured.name"] = "T Nkosi" },
        }, idempotent: true);
        generated.StatusCode.ShouldBe(HttpStatusCode.Created);
        var document = await generated.Json();
        document.GetProperty("templateVersion").GetInt32().ShouldBe(1);
        document.GetProperty("productVersionRef").GetString().ShouldBe("LIF-TERM v1");
        document.GetProperty("retentionUntil").GetString().ShouldBe(TestClock.Today.AddYears(5).ToString("yyyy-MM-dd"));
        var url = $"/api/v1/documents/documents/{document.GetProperty("id").GetGuid()}";

        (await client.GetAsync($"{url}/content")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var content = await client.GetAsync($"{url}/content?purpose=Customer%20query");
        content.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await content.Content.ReadAsStringAsync()).ShouldContain("policy.number: P-1");

        (await (await client.Send(HttpMethod.Post, $"{url}/deliveries", new { channel = "EMAIL", recipient = "t@example.com" }, idempotent: true)).Json())
            .GetProperty("status").GetString().ShouldBe("SENT");
        (await (await client.GetAsync(url)).Json()).GetProperty("status").GetString().ShouldBe("Delivered");
        (await (await client.Send(HttpMethod.Post, $"{url}/dispose", ifMatch: "\"2\"")).Code()).ShouldBe("DOCUMENT_RETENTION_NOT_ENDED");

        var missing = await client.Send(HttpMethod.Post, "/api/v1/documents/documents/generate", new
        {
            templateCode = "TPL-SCH", language = "en", categoryCode = "POLICY_SCHEDULE", businessRefType = "POLICY", businessRefId = Guid.CreateVersion7(),
            data = new Dictionary<string, object> { ["policy.number"] = "P-2" },
        }, idempotent: true);
        (await missing.Code()).ShouldBe("DOCUMENT_MERGE_FIELD_MISSING");
    }

    [Fact]
    public async Task HighlySensitiveDocument_IsReadOnlyByItsAllowedRoles_AndEveryReadIsRecorded_DOC003_DOC004()
    {
        var client = _factory.ClientFor(TenantA, "shared-user");
        var stored = await client.Send(HttpMethod.Post, "/api/v1/documents/documents", new
        {
            categoryCode = "MEDICAL_EVIDENCE", businessRefType = "CLAIM", businessRefId = Guid.CreateVersion7(), mimeType = "text/plain",
            contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Medical report")),
        }, idempotent: true);
        stored.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await stored.Json()).GetProperty("id").GetGuid();
        var url = $"/api/v1/documents/documents/{id}/content?purpose=Assess%20claim";

        (await (await client.GetAsync(url)).Code()).ShouldBe("DOCUMENT_ROLE_REQUIRED");
        var assessor = _factory.ClientFor(TenantA, "claims-assessor");
        (await (await assessor.GetAsync(url)).Content.ReadAsStringAsync()).ShouldBe("Medical report");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM document.document_access_log WHERE document_id = @d AND purpose = 'Assess claim'", ("d", id)))
            .ShouldBe(1);

        // Every read leaves exactly one audit entry; a refused one (no allowed role, no purpose) is DENIED with why.
        (await (await assessor.GetAsync($"/api/v1/documents/documents/{id}/content")).Code()).ShouldBe("DOCUMENT_PURPOSE_REQUIRED");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT string_agg(concat_ws('|', outcome, coalesce(details->>'purpose', '-'), coalesce(details->>'refused', '-')), ',' ORDER BY occurred_at)
            FROM audit.audit_event WHERE tenant_id = @t AND operation = 'ReadDocument' AND target_id = @d
            """, ("t", TenantA), ("d", id.ToString()))).ShouldBe("DENIED|Assess claim|ROLE_REQUIRED,SUCCESS|Assess claim|-,DENIED|-|PURPOSE_REQUIRED");

        var hold = await (await client.Send(HttpMethod.Post, $"/api/v1/documents/documents/{id}/legal-holds", new { reasonCode = "LITIGATION" })).Json();
        (await (await client.GetAsync($"/api/v1/documents/documents/{id}")).Json()).GetProperty("legalHold").GetBoolean().ShouldBeTrue();
        // Releasing it needs a second person (OQ-111): the request is accepted and the hold stays in force until approved.
        (await client.Send(HttpMethod.Post, $"/api/v1/documents/documents/{id}/legal-holds/{hold.GetProperty("id").GetGuid()}/release", new { reason = "Settled" }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await (await client.GetAsync($"/api/v1/documents/documents/{id}")).Json()).GetProperty("legalHold").GetBoolean().ShouldBeTrue();
    }

    private static async Task<Guid> Register(HttpClient client, string firstNames, string surname)
    {
        var response = await client.Send(HttpMethod.Post, "/api/v1/party/parties", new
        {
            individual = new { firstNames, surname, birthDate = "1990-04-02", nationality = "ZA" },
            confirmNew = true, reason = "A separate test person",
        }, idempotent: true);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Json()).GetProperty("id").GetGuid();
    }

    private Task Static(string table, string code, string columns, string values, bool reference = false) =>
        fixture.Database.ExecuteAsOwner($"""
            INSERT INTO {table} (tenant_id, code, version, effective_from, record_status, created_by, change_reason, {columns})
            VALUES (@t, @code, 1, current_date - 1, '{(reference ? "ACTIVE" : "EFFECTIVE")}', 'seed', 'test seed', {values})
            ON CONFLICT DO NOTHING
            """, ("t", TenantA), ("code", code));
}
