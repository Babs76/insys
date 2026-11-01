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
using System.Net.Http.Json;
using Insys.Health.Contracts;
using Insys.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-124 (ADR-025): a claim's date of service is judged by the tenant's business date, at the day boundary of its own time
/// zone. Each host runs on a fixed clock, so the real conversion is used (no test date). Tenant A is in Africa/Johannesburg
/// (UTC+2), tenant B in Pacific/Kiritimati (UTC+14). The refusal comes before the claim's membership is read, so any other
/// outcome means the date was accepted. General and Life refuse on the same date in their domain tests.
/// </summary>
public sealed class FutureClaimDateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Future = "CLAIM_EVENT_DATE_IN_FUTURE";
    private static readonly DateOnly From = new(2026, 1, 1);

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        foreach (var (tenant, zone) in new[] { (DatabaseFixture.TenantA, "Africa/Johannesburg"), (DatabaseFixture.TenantB, "Pacific/Kiritimati") })
        {
            await db.SeedStatic(tenant, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-BR124", From, ("tenant", "BR124"), ("base_currency", "ZAR"), ("languages", new[] { "en" }),
                ("default_language", "en"), ("time_zone", zone), ("country", "ZA"));
            await db.UserWithPermissions(tenant, "claim-dater", HealthPermissions.ClaimCreate);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    // Johannesburg: the 1st until 21:59:59 UTC, the 2nd from 22:00.
    [InlineData("A", "2026-11-01T21:59:59Z", "2026-11-01", "2026-11-02")]
    [InlineData("A", "2026-11-01T22:00:00Z", "2026-11-02", "2026-11-03")]
    // Kiritimati: the 1st until 09:59:59 UTC, the 2nd from 10:00, while the UTC date is still the 1st.
    [InlineData("B", "2026-11-01T09:59:59Z", "2026-11-01", "2026-11-02")]
    [InlineData("B", "2026-11-01T10:00:00Z", "2026-11-02", "2026-11-03")]
    public async Task AServiceDate_IsAcceptedOnTheTenantsToday_AndRefusedTheDayAfter_BR124(string which, string at, string today, string tomorrow)
    {
        await using var host = new InsysApiFactory(fixture.Database, time: new FakeTimeProvider(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
        var client = host.ClientFor(which == "A" ? DatabaseFixture.TenantA : DatabaseFixture.TenantB, "claim-dater");

        (await Submit(client, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture))).ShouldNotBe(Future);
        var refused = await client.PostAsJsonAsync("/api/v1/health/claims", Claim(DateOnly.Parse(tomorrow, System.Globalization.CultureInfo.InvariantCulture)));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Code()).ShouldBe(Future);
    }

    private static async Task<string?> Submit(HttpClient client, DateOnly day) => await (await client.PostAsJsonAsync("/api/v1/health/claims", Claim(day))).Code();

    private static object Claim(DateOnly day) => new
    {
        source = "MEMBER", membershipId = Guid.CreateVersion7(), patientPartyId = Guid.CreateVersion7(), serviceDate = day, formatCode = "API", currency = "ZAR",
        lines = new[] { new { tariffCode = "0300", quantity = 1, claimedAmount = 500m } },
    };
}
