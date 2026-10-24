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

/// <summary>
/// addenda/health-plan-products.md §1: a plan option names the Product Factory product it is sold as, and every option
/// of a plan names the same one, checked when a version is submitted.
/// </summary>
public sealed class HealthPlanProductTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task APlanOption_NamesItsProduct_TheSameForEveryOptionOfThePlan_OQ080()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "plan-maker", StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update);
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "plan-maker");

        (await Submit(maker, "PP-A", "PLAN-P", "A", null)).ShouldBe("HEALTH_PLAN_PRODUCT_INVALID");
        (await Submit(maker, "PP-B", "PLAN-P", "B", "HLT-ONE")).ShouldBe("SUBMITTED");
        (await Submit(maker, "PP-C", "PLAN-P", "C", "HLT-TWO")).ShouldBe("HEALTH_PLAN_PRODUCT_INVALID");
        (await Submit(maker, "PP-D", "PLAN-P", "D", "HLT-ONE")).ShouldBe("SUBMITTED");
    }

    private static async Task<string?> Submit(HttpClient maker, string code, string plan, string option, string? product)
    {
        const string url = "/api/v1/health/static/plans-and-options";
        var created = await maker.Send(HttpMethod.Post, url, new
        {
            code, planCode = plan, optionCode = option, benefitSchedule = "BS-1", network = "NET-1", benefitYear = "CALENDAR", memberClaimBasis = "REFERENCE_TARIFF", productCode = product,
            effectiveFrom = Today.AddDays(1), changeReason = "Plans",
        }, idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var version = (await created.Json()).GetProperty("version").GetInt32();
        var submitted = await maker.Send(HttpMethod.Post, $"{url}/{code}/versions/{version}/submit", ifMatch: created.ETag());
        var body = await submitted.Json();
        return submitted.IsSuccessStatusCode ? body.GetProperty("recordStatus").GetString() : body.GetProperty("code").GetString();
    }
}
