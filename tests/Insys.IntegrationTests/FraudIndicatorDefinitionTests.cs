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

/// <summary>F009: a fraud indicator whose logic is not a valid expression is refused when drafted, in the Claims Framework and in Health.</summary>
public sealed class FraudIndicatorDefinitionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Theory]
    [InlineData("/api/v1/claims-framework/static/fraud-indicator-definitions", "referralThreshold")]
    [InlineData("/api/v1/health/static/health-fraud-indicator-definitions", "threshold")]
    public async Task AnIndicatorWithInvalidLogic_IsRefusedWhenDrafted_F009(string url, string thresholdField)
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "fraud-author", StaticDataPermissions.Read, StaticDataPermissions.Create,
            StaticDataPermissions.Update);
        var client = factory.ClientFor(DatabaseFixture.TenantA, "fraud-author");

        var refused = await client.Send(HttpMethod.Post, url, Indicator("FI-BAD", "days_since_inception == 3", thresholdField), idempotent: true);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync());
        (await refused.Json()).GetProperty("errors").TryGetProperty("logic", out _).ShouldBeTrue();

        (await client.Send(HttpMethod.Post, url, Indicator("FI-OK", "days_since_inception < 30", thresholdField), idempotent: true)).StatusCode
            .ShouldBe(HttpStatusCode.Created);
    }

    private static Dictionary<string, object> Indicator(string code, string logic, string thresholdField) => new()
    {
        ["code"] = code, ["indicatorCode"] = code, ["logic"] = logic, ["score"] = 10, [thresholdField] = 100, ["referralQueue"] = "FRAUD",
        ["effectiveFrom"] = TestClock.Today, ["changeReason"] = "Test indicator",
    };
}
