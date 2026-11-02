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
using Insys.Seed;
using Insys.Testing;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>Loaders ended while loading is still to do are put back by the routes an administrator uses, never around them.</summary>
public sealed class OftzaSeedRestoreTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private sealed class Interrupted() : Exception("Stopped part-way, for the test.");

    [Fact]
    public async Task LoadersEndedBeforeLoadingFinished_ComeBackByTheGovernedRoutes_AndEndDisabled()
    {
        var db = fixture.Database;
        var host = await fixture.Host();
        string[] platform = ["tenant.read", "tenant.create", "tenant.update", "user.read", "user.create", "approval.read", "approval.approve", "static_data.read", "static_data.create", "static_data.update", "static_data.approve"];
        await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-1", platform);
        var second = await db.UserWithPermissions(DatabaseFixture.Operator, "seed-platform-2", platform);
        var role = await db.RoleWithPermissions(DatabaseFixture.Operator);
        await db.AssignRole(DatabaseFixture.Operator, second, role);
        await db.SeedStatic(DatabaseFixture.Operator, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", "AUTH-SEED-ADM", new DateOnly(2026, 1, 1), ("role_code", role),
            ("transaction_type", "TENANT_ADMINISTRATOR"), ("limit_value", 1m), ("delegation_allowed", false));

        var calls = new List<string>();
        var stopped = false;
        void Watch(string call)
        {
            calls.Add(call);
            if (!stopped && call.StartsWith("POST ", StringComparison.Ordinal) && call.EndsWith("party/parties", StringComparison.Ordinal))
            {
                stopped = true;
                throw new Interrupted();
            }
        }

        OftzaSeed Seed() => new(new SeedContext(new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-1"), Watch), new Persona(host.ClientFor(DatabaseFixture.Operator, "seed-platform-2"), Watch),
            (tenant, subject) => new Persona(host.ClientFor(tenant, subject), Watch), () => host.DrainOutbox()));

        var first = Seed();
        await Should.ThrowAsync<Interrupted>(first.Run);
        var t = ("t", (object)first.TenantId);

        // An administrator ends both while loading is still to do: one user disabled, the other's role revoked.
        var admin = new Persona(host.ClientFor(first.TenantId, OftzaSeed.FirstAdministrator));
        async Task<string> Etag(string url) => $"\"{(await admin.Send(HttpMethod.Get, url)).GetProperty("rowVersion").GetInt64()}\"";
        var disabled = $"/api/v1/identity/users/{await db.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = 'oftza-seed-loader-1'", t)}";
        var revoked = $"/api/v1/identity/users/{await db.ScalarAsOwner<Guid>("SELECT id FROM iam.user_account WHERE tenant_id = @t AND username = 'oftza-seed-loader-2'", t)}";
        await admin.Send(HttpMethod.Post, $"{disabled}/disable", new { reason = "Ended early, for the test." }, await Etag(disabled), idempotent: false);
        await admin.Send(HttpMethod.Post, $"{revoked}/revoke-role", new { roleCode = "OFTZA_SEED_LOADER" }, await Etag(revoked), idempotent: false);
        await host.DrainOutbox();

        calls.Clear();
        await Seed().Run();

        calls.ShouldContain("POST /api/v1/identity/users", "a disabled user stays disabled, so an administrator makes its replacement");
        calls.ShouldContain($"POST {revoked}/assign-role", "the revoked role is assigned again");
        calls.ShouldNotContain($"POST {disabled}/assign-role");
        (await db.ScalarAsOwner<string>("SELECT status FROM tenant.tenant WHERE id = @t", t)).ShouldBe("ACTIVE");
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM party.party WHERE tenant_id = @t", t)).ShouldBe(25L);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username = 'oftza-seed-loader-1-2' AND status = 'DISABLED'", t)).ShouldBe(1L);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_account WHERE tenant_id = @t AND username LIKE 'oftza-seed-loader-%' AND status <> 'DISABLED'", t)).ShouldBe(0L);
        (await db.ScalarAsOwner<long>("SELECT count(*) FROM iam.user_role_assignment WHERE tenant_id = @t AND role_code = 'OFTZA_SEED_LOADER' AND status <> 'REVOKED'", t)).ShouldBe(0L);
    }
}

/// <summary>Told to slow down, the seed waits as long as the API says and asks again. It gets no exemption from the limit.</summary>
public sealed class SeedRateLimitTests
{
    private sealed class Limited(params TimeSpan?[] waits) : HttpMessageHandler
    {
        private int _call;

        public int Calls => _call;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_call < waits.Length ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (_call < waits.Length && waits[_call] is { } wait)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            }

            _call++;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A429_IsWaitedOutForTheRetryAfterItGives_ThenAskedAgain()
    {
        var limited = new Limited(TimeSpan.FromSeconds(2));
        var persona = new Persona(new HttpClient(limited) { BaseAddress = new Uri("http://seed.test") });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await persona.Send(HttpMethod.Get, "/anything");

        limited.Calls.ShouldBe(2);
        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
    }

    [Fact]
    public async Task A429WithoutRetryAfter_BacksOff_ThenAskedAgain()
    {
        var limited = new Limited((TimeSpan?)null);
        var persona = new Persona(new HttpClient(limited) { BaseAddress = new Uri("http://seed.test") });
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await persona.Send(HttpMethod.Get, "/anything");

        limited.Calls.ShouldBe(2);
        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4.9));
    }
}
