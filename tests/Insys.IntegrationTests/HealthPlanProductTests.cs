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
    private static readonly DateOnly Today = TestClock.Today;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task APlanOption_NamesItsProduct_TheSameForEveryOptionOfThePlan_OQ080()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "plan-maker", StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update);
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "plan-maker");

        // BR-084: the product is mandatory, so a draft without one is refused when it is saved, with the field named.
        (await Submit(maker, "PP-A", "PLAN-P", "A", null)).ShouldBe("VALIDATION_FAILED productCode");
        (await Submit(maker, "PP-B", "PLAN-P", "B", "HLT-ONE")).ShouldBe("SUBMITTED");
        (await Submit(maker, "PP-C", "PLAN-P", "C", "HLT-TWO")).ShouldBe("HEALTH_PLAN_PRODUCT_INVALID");
        (await Submit(maker, "PP-D", "PLAN-P", "D", "HLT-ONE")).ShouldBe("SUBMITTED");
        (await Submit(maker, "PP-E", "PLAN-P", "E", "HLT-ONE", dropProductBeforeSubmit: true)).ShouldBe("HEALTH_PLAN_PRODUCT_INVALID",
            "a draft saved without a product before BR-084 is still refused when submitted");
    }

    private async Task<string?> Submit(HttpClient maker, string code, string plan, string option, string? product, bool dropProductBeforeSubmit = false)
    {
        const string url = "/api/v1/health/static/plans-and-options";
        var created = await maker.Send(HttpMethod.Post, url, new
        {
            code, planCode = plan, optionCode = option, benefitSchedule = "BS-1", network = "NET-1", benefitYear = "CALENDAR", memberClaimBasis = "REFERENCE_TARIFF", productCode = product,
            effectiveFrom = Today.AddDays(1), changeReason = "Plans",
        }, idempotent: true);
        if (created.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var refused = await created.Json();
            return $"{refused.GetProperty("code").GetString()} {string.Join(",", refused.GetProperty("errors").EnumerateObject().Select(e => e.Name))}";
        }

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var version = (await created.Json()).GetProperty("version").GetInt32();
        if (dropProductBeforeSubmit)
        {
            await fixture.Database.ExecuteAsOwner("UPDATE health.cfg_plans_and_options SET product_code = NULL WHERE tenant_id = @t AND code = @c",
                ("t", DatabaseFixture.TenantA), ("c", code));
        }

        var submitted = await maker.Send(HttpMethod.Post, $"{url}/{code}/versions/{version}/submit", ifMatch: created.ETag());
        var body = await submitted.Json();
        return submitted.IsSuccessStatusCode ? body.GetProperty("recordStatus").GetString() : body.GetProperty("code").GetString();
    }
}
