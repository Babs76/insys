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
using Insys.BuildingBlocks.Api.StaticData;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>GAP-024: a rating test case without currency and frequency in its inputs is refused when drafted (PRD-005).</summary>
public sealed class RatingTestCaseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Url = "/api/v1/rating/static/rating-test-case-library";

    [Fact]
    public async Task ATestCase_NeedsCurrencyAndFrequency_InItsInputs_GAP024()
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "rating-author", StaticDataPermissions.Read, StaticDataPermissions.Create);
        var client = factory.ClientFor(DatabaseFixture.TenantA, "rating-author");

        var refused = await client.Send(HttpMethod.Post, Url, Case("TC-NO-FREQ", new { currency = "ZAR", sum_insured = 100_000 }), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("VALIDATION_FAILED");
        problem.GetProperty("errors").GetProperty("inputs")[0].GetString()!.ShouldContain("frequency");

        (await (await client.Send(HttpMethod.Post, Url, Case("TC-BAD-CCY", new { currency = "rand", frequency = "ANNUAL" }), idempotent: true)).Json())
            .GetProperty("errors").GetProperty("inputs")[0].GetString()!.ShouldContain("currency");

        (await client.Send(HttpMethod.Post, Url, Case("TC-OK", new { currency = "ZAR", frequency = "ANNUAL", sum_insured = 100_000 }), idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private static object Case(string code, object inputs) => new
    {
        code, caseId = code, productVersion = "GEN-MOTOR-1", inputs, expectedPremium = 3_050m, tolerance = 0.01m, effectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow),
        changeReason = "Test case",
    };
}
