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
using Insys.Tenancy.Contracts;
using Insys.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// What every error response carries for the front end: the correlation ID in the X-Correlation-Id header and the
/// ProblemDetails body alike (GAP-007), and Retry-After on 429 (GAP-006) as on the warm-up gate's 503. One API host per
/// class: only one instance may run against a database (ADR-006).
/// </summary>
public sealed class RateLimitResponseTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task TooManyRequests_Is429_WithRetryAfterInWholeSeconds()
    {
        var factory = await fixture.Host(new Dictionary<string, string?> { ["RateLimiting:PermitsPerMinute"] = "1" });
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "limited-user");
        var client = factory.ClientFor(DatabaseFixture.TenantB, "limited-user");

        await client.GetAsync("/api/v1/identity/me");
        var refused = await client.GetAsync("/api/v1/identity/me");

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync()).ShouldContain("\"code\":\"RATE_LIMITED\"");
        var retryAfter = refused.Headers.RetryAfter?.Delta;
        retryAfter.ShouldNotBeNull();
        retryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        await ErrorResponse.ShouldCarryTheCorrelationId(refused);
    }
}

/// <summary>The correlation ID on errors from authentication, authorisation and routing (GAP-007).</summary>
public sealed class ErrorCorrelationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task NotFoundForbiddenAndUnauthorised_CarryTheCorrelationId_InHeaderAndBody()
    {
        var factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "error-clerk");
        var clerk = factory.ClientFor(DatabaseFixture.TenantA, "error-clerk");

        await ErrorResponse.ShouldCarryTheCorrelationId(await clerk.GetAsync($"/api/v1/integration/dead-letters/{Guid.CreateVersion7()}"), HttpStatusCode.Forbidden);
        await ErrorResponse.ShouldCarryTheCorrelationId(await factory.CreateClient().GetAsync("/api/v1/identity/me"), HttpStatusCode.Unauthorized);
        await ErrorResponse.ShouldCarryTheCorrelationId(await clerk.GetAsync("/api/v1/no-such-endpoint"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheCallersCorrelationId_IsEchoedOnAnError()
    {
        var factory = await fixture.Host();
        var supplied = Guid.CreateVersion7();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
        request.Headers.Add(ErrorResponse.Header, supplied.ToString());

        var response = await factory.CreateClient().SendAsync(request);

        (await ErrorResponse.ShouldCarryTheCorrelationId(response, HttpStatusCode.Unauthorized)).ShouldBe(supplied);
    }
}

/// <summary>The correlation ID on errors written outside the endpoints: an unhandled exception, and the warm-up gate.</summary>
public sealed class ErrorCorrelationOwnHostTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AnUnexpectedFailure_Is500_WithTheCorrelationId_AndNoInternalDetail()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "error-victim");
        await using var factory = new InsysApiFactory(fixture.Database);
        await using var failing = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ITenantProfiles, FailingTenantProfiles>()));
        var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(DatabaseFixture.TenantA, "error-victim"));

        var response = await client.GetAsync("/api/v1/identity/me");

        await ErrorResponse.ShouldCarryTheCorrelationId(response, HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(FailingTenantProfiles.Message);
    }

    [Fact]
    public async Task TheWarmupGates503_CarriesTheCorrelationId()
    {
        await using var factory = new InsysApiFactory(fixture.Database, waitForWarmup: false);
        var client = factory.CreateClient();

        // Answered by the gate while endpoints are still being built, or normally once they are: only the 503 is checked.
        var response = await client.GetAsync("/health/ready");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            response.Headers.RetryAfter.ShouldNotBeNull();
            await ErrorResponse.ShouldCarryTheCorrelationId(response);
        }
    }

    private sealed class FailingTenantProfiles : ITenantProfiles
    {
        public const string Message = "simulated tenancy store failure";

        public Task<TenantProfile?> Get(Guid tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);
    }
}

internal static class ErrorResponse
{
    public const string Header = "X-Correlation-Id";

    public static async Task<Guid> ShouldCarryTheCorrelationId(HttpResponseMessage response, HttpStatusCode? status = null)
    {
        if (status is { } expected)
        {
            response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
        }

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var header = Guid.Parse(response.Headers.GetValues(Header).Single());
        System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("correlationId").GetGuid().ShouldBe(header);
        return header;
    }
}
