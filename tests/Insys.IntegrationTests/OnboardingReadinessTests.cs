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
using Insys.Tenancy.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Onboarding readiness (docs/specs/addenda/onboarding-readiness.md, TEN-008): a step completes only when its configuration
/// is in force in the tenant; a refusal names every failing check and what is missing; waivers and evidence stand in
/// only where the addendum allows them.
/// </summary>
public sealed class OnboardingReadinessTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string OperatorAdmin = "readiness-operator";
    private static readonly DateOnly From = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);
    private InsysApiFactory _factory = null!;
    private HttpClient _operator = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, OperatorAdmin, TenancyPermissions.Read, TenancyPermissions.Create, TenancyPermissions.Update);
        _operator = _factory.ClientFor(DatabaseFixture.Operator, OperatorAdmin);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AStep_CompletesOnlyWhenItsConfigurationIsInForce_ListingEveryFailingCheck_TEN008()
    {
        var (tenantId, url) = await Register("READY");
        var readiness = await Readiness(url);
        Check(readiness, "TENANT_REGISTRATION", "LOCALE_DEFAULTS").GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldBe(["tenant.cfg_locale_defaults"]);
        Check(readiness, "TENANT_REGISTRATION", "TIME_ZONE").GetProperty("ready").GetBoolean().ShouldBeTrue("given at registration");

        // Everything configured except the first administrator and one staff role's channel row.
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.SeedStaticFilled(tenantId, "iam.cfg_roles_and_permission_sets", "EFFECTIVE", "UNDERWRITER", From, ("role_code", "UNDERWRITER"),
            ("permissions", new[] { "general_quote.create", "general_quote.read" }));
        var etag = await Complete(url, (await _operator.GetAsync(url)).ETag(), "PLATFORM_REFERENCE_DATA");
        etag = await Complete(url, etag, "TENANT_REGISTRATION");
        var refused = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", OnboardingSeed.StepBody("ORGANISATION_AND_ACCESS"), ifMatch: etag);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Json();
        problem.GetProperty("code").GetString().ShouldBe("ONBOARDING_STEP_NOT_READY");
        var errors = problem.GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).Order().ShouldBe(["CHANNELS", "FIRST_ADMINISTRATOR"]);
        errors.GetProperty("CHANNELS").EnumerateArray().Select(m => m.GetString()).ShouldBe(["UNDERWRITER: general_quote.create"], "reads are not channel-scoped");
        (await (await _operator.GetAsync(url)).Json()).GetProperty("onboardingStep").GetString().ShouldBe("ORGANISATION_AND_ACCESS", "nothing changed");

        var admin = await fixture.Database.UserWithPermissions(tenantId, "ready-admin", "user.read");
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET role_code = 'TENANT_ADMIN' WHERE user_id = @u", ("u", admin));
        await fixture.Database.SeedStaticFilled(tenantId, "channel.cfg_channel_transaction_permissions", "EFFECTIVE", "CP-UW", From, ("channel_code", "STF"),
            ("role_code", "UNDERWRITER"), ("transaction_type", "general_quote.create"), ("product_code", DBNull.Value), ("allowed", true),
            ("requires_approval", false));
        etag = await Complete(url, etag, "ORGANISATION_AND_ACCESS");
        foreach (var step in new[] { "FINANCIAL_FOUNDATIONS", "SHARED_OPERATIONAL_SETUP", "PAYMENTS_AND_DISTRIBUTION", "DOMAIN_REFERENCE_DATA", "PRODUCT_FACTORY" })
        {
            etag = await Complete(url, etag, step);
        }

        // Reinsurance: no treaties, so the step needs "no reinsurance" with a reason; only the checks that allow it take one.
        var noWaiver = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", new { step = "REINSURANCE" }, ifMatch: etag);
        (await noWaiver.Json()).GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["TREATIES"]);
        var wrongCheck = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step",
            new { step = "REINSURANCE", attestations = new[] { new { check = "SMOKE_RUN", note = "RUN-1" } } }, ifMatch: etag);
        (await wrongCheck.Code()).ShouldBe("ONBOARDING_ATTESTATION_NOT_ALLOWED");
        foreach (var step in new[] { "REINSURANCE", "INTEGRATION_AND_REPORTING", "END_TO_END_TESTS" })
        {
            etag = await Complete(url, etag, step);
        }

        (await _operator.Send(HttpMethod.Post, $"{url}/activate", new { reason = "Go live" }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var done = await Readiness(url);
        done.GetProperty("steps").EnumerateArray().ShouldAllBe(s => s.GetProperty("completed").GetBoolean() && s.GetProperty("ready").GetBoolean());
        var waived = Check(done, "REINSURANCE", "TREATIES").GetProperty("attestation");
        (waived.GetProperty("kind").GetString(), waived.GetProperty("note").GetString()).ShouldBe(("WAIVER", "No reinsurance: test tenant"));
    }

    [Fact]
    public async Task AgeingBuckets_MustCoverEveryDayToTheLastAction_Request46()
    {
        var (tenantId, url) = await Register("GAPPY");
        await fixture.Database.SeedReadyTenant(tenantId, From);
        await fixture.Database.SeedStaticFilled(tenantId, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B3", From, ("bucket_code", "B3"), ("days_from", 25),
            ("days_to", 26), ("action", "REMINDER"));
        var check = Check(await Readiness(url), "PAYMENTS_AND_DISTRIBUTION", "COLLECTION_METHODS");
        check.GetProperty("ready").GetBoolean().ShouldBeFalse();
        check.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ShouldContain("ageing buckets overlap from day 25");
    }

    private async Task<(Guid TenantId, string Url)> Register(string code)
    {
        var registered = await _operator.Send(HttpMethod.Post, "/api/v1/tenancy/tenants",
            new { tenantCode = code, legalName = code + " Ltd", isolationProfile = "B", timeZone = "Africa/Johannesburg" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var id = (await registered.Json()).GetProperty("id").GetGuid();
        return (id, $"/api/v1/tenancy/tenants/{id}");
    }

    private async Task<JsonElement> Readiness(string url)
    {
        var response = await _operator.GetAsync($"{url}/readiness");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    private static JsonElement Check(JsonElement readiness, string step, string code) =>
        readiness.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == step)
            .GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    private async Task<string> Complete(string url, string etag, string step)
    {
        var completed = await _operator.Send(HttpMethod.Post, $"{url}/complete-onboarding-step", OnboardingSeed.StepBody(step), ifMatch: etag);
        completed.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{step}: {await completed.Content.ReadAsStringAsync()}");
        return completed.ETag();
    }
}
