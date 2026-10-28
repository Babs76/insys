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
using Insys.ClaimsFramework.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-039: recording evidence has its own schema, which requires the stored document, and is refused without one;
/// requesting evidence takes only its type. The fraud indicators in force are listed with the facts each reads, by the
/// names ClaimsEvaluateFraud takes them.
/// </summary>
public sealed class ClaimsEvidenceAndFraudTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private static readonly DateOnly From = new(2026, 1, 1);
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(T, "br039-clerk", ClaimsPermissions.ClaimRead, ClaimsPermissions.ClaimUpdate);
        _clerk = _factory.ClientFor(T, "br039-clerk");
        await fixture.Database.SeedStatic(T, "claims.ref_evidence_types", "ACTIVE", "BR39_PHOTOS", From, ("document_category", "CLAIM_EVIDENCE"),
            ("mandatory_for", Array.Empty<string>()));
        await fixture.Database.SeedStatic(T, "claims.cfg_fraud_indicator_definitions", "EFFECTIVE", "BR39-EARLY", From, ("indicator_code", "BR39_EARLY_CLAIM"),
            ("logic", "days_since_inception < 30 and sum_insured > 100000"), ("score", 40), ("referral_threshold", 100), ("referral_queue", "FRAUD"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RecordingEvidence_RequiresItsDocument_AndRequestingTakesOnlyTheType_BR039()
    {
        var number = await Claim();
        (await _clerk.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/evidence-requests", new { evidenceType = "BR39_PHOTOS" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var missing = await _clerk.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/evidence", new { evidenceType = "BR39_PHOTOS" }, idempotent: true);
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await missing.Content.ReadAsStringAsync());
        (await missing.Content.ReadAsStringAsync()).ShouldContain("documentId");

        var document = Guid.CreateVersion7();
        var recorded = await _clerk.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/evidence", new { evidenceType = "BR39_PHOTOS", documentId = document },
            idempotent: true);
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, await recorded.Content.ReadAsStringAsync());
        (await recorded.Json()).GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("documentId").ToString()).ShouldContain(document.ToString());
    }

    [Fact]
    public async Task TheFraudIndicators_AreListedWithTheFactsTheyRead_BR039()
    {
        var listed = await _clerk.GetAsync("/api/v1/claims/fraud-indicators");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());
        var indicator = (await listed.Json()).EnumerateArray().Single(i => i.GetProperty("indicatorCode").GetString() == "BR39_EARLY_CLAIM");
        indicator.GetProperty("facts").EnumerateArray().Select(f => f.GetString()).ShouldBe(["days_since_inception", "sum_insured"]);
        indicator.TryGetProperty("logic", out _).ShouldBeFalse("the condition is not shown");

        // Given only one of its facts, the indicator is skipped and the missing one recorded (F009).
        var number = await Claim();
        var evaluated = await _clerk.Send(HttpMethod.Post, $"/api/v1/claims/claims/{number}/fraud-evaluations", new { days_since_inception = 3 }, idempotent: true);
        evaluated.StatusCode.ShouldBe(HttpStatusCode.OK, await evaluated.Content.ReadAsStringAsync());
        var skipped = (await evaluated.Json()).GetProperty("fraudEvaluations").EnumerateArray().Last().GetProperty("skipped").EnumerateArray()
            .Single(s => s.GetProperty("indicatorCode").GetString() == "BR39_EARLY_CLAIM");
        skipped.GetProperty("missingFacts").EnumerateArray().Select(f => f.GetString()).ShouldBe(["sum_insured"]);
    }

    private async Task<string> Claim()
    {
        var number = $"BR39-{Guid.NewGuid():N}"[..20];
        await new RowFiller(fixture.Database, DateTimeOffset.UtcNow).Insert(T, "claims.claim_register", ("claim_number", number), ("domain", "GENERAL"),
            ("registered_at", DateTimeOffset.UtcNow));
        return number;
    }
}
