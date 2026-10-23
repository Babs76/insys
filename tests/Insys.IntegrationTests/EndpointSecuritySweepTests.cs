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
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// SRS §27.4 across every API endpoint, not a sample: each is called without a token, without a tenant, without its
/// permission, with a token for a tenant the user does not belong to, and (for platform endpoints) from a tenant. Every
/// call must be refused before the endpoint runs (IAM-007, TEN-002, TEN-003, TEN-007), so nothing is written or read.
/// </summary>
public sealed partial class EndpointSecuritySweepTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Nobody = "sweep-nobody";
    private const string Reader = "sweep-reader";
    private const string PlatformInTenant = "sweep-platform-in-tenant";
    private InsysApiFactory _factory = null!;
    private List<(string Method, string Url, RouteEndpoint Endpoint)> _calls = [];

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host(new Dictionary<string, string?> { ["RateLimiting:PermitsPerMinute"] = "1000000" });
        _ = _factory.CreateClient();
        _calls = Calls(_factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true));
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Nobody);

        // Every read permission there is, and every platform permission, held in tenant A.
        var permissions = _calls.Select(c => c.Endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission).Distinct().ToList();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Reader, [.. permissions.Where(p => p.EndsWith(".read", StringComparison.Ordinal))]);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, PlatformInTenant,
            [.. _calls.Where(c => c.Endpoint.Metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is not null)
                .Select(c => c.Endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission).Distinct()]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void TheSweep_CoversEveryApiEndpoint()
    {
        // Hundreds of endpoints, generated static data ones included; each module's own endpoints are among them.
        _calls.Count.ShouldBeGreaterThan(1_000);
        foreach (var route in new[] { "/api/v1/distribution/statements", "/api/v1/reinsurance/bordereaux", "/api/v1/compliance/returns", "/api/v1/analytics/projections/rebuild",
                     "/api/v1/integration/webhook-deliveries", "/api/v1/health/claims", "/api/v1/life/claims", "/api/v1/general/claims" })
        {
            _calls.ShouldContain(c => c.Url.StartsWith(route, StringComparison.Ordinal), route);
        }
    }

    [Fact]
    public async Task EveryEndpoint_WithoutAToken_Is401_IAM007() =>
        (await Refusals(_factory.CreateClient(), _calls, HttpStatusCode.Unauthorized)).ShouldBeEmpty();

    [Fact]
    public async Task EveryEndpoint_WithATokenWithoutATenant_IsRefused_TEN002()
    {
        var client = Client(null, Nobody);
        (await Refusals(client, _calls, HttpStatusCode.Forbidden)).ShouldBeEmpty();
    }

    /// <summary>Every endpoint names a permission, except those about the caller (/identity/me), which any user of the tenant may use.</summary>
    [Fact]
    public async Task EveryEndpoint_WithoutItsPermission_Is403_IAM007() =>
        (await Refusals(Client(DatabaseFixture.TenantA, Nobody), _calls.Where(c => !SignedInUserOnly(c.Endpoint)), HttpStatusCode.Forbidden)).ShouldBeEmpty();

    /// <summary>A user holding every read permission reaches no endpoint that changes anything (privilege escalation).</summary>
    [Fact]
    public async Task EveryChangingEndpoint_IsRefusedToAUserWithEveryReadPermission_PrivilegeEscalation()
    {
        var changes = _calls.Where(c => !c.Endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission.EndsWith(".read", StringComparison.Ordinal)
                                        && c.Endpoint.Metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is null && !SignedInUserOnly(c.Endpoint)).ToList();
        changes.Count.ShouldBeGreaterThan(500);
        (await Refusals(Client(DatabaseFixture.TenantA, Reader), changes, HttpStatusCode.Forbidden)).ShouldBeEmpty();
    }

    /// <summary>A token naming a tenant the user does not belong to is refused (TEN-003), even for reads the user holds in their own tenant.</summary>
    [Fact]
    public async Task EveryEndpoint_WithATokenForAnotherTenant_Is403_TEN003()
    {
        var reads = _calls.Where(c => c.Endpoint.Metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is null).ToList();
        (await Refusals(Client(DatabaseFixture.TenantB, Reader), reads, HttpStatusCode.Forbidden)).ShouldBeEmpty();
    }

    /// <summary>Platform administration is for the platform operator tenant only, whatever permissions a tenant's user holds (TEN-007).</summary>
    [Fact]
    public async Task EveryPlatformEndpoint_FromATenant_IsRefused_EvenWithItsPermission_TEN007()
    {
        var platform = _calls.Where(c => c.Endpoint.Metadata.GetMetadata<PlatformOperatorEndpointMetadata>() is not null).ToList();
        platform.ShouldNotBeEmpty();
        (await Refusals(Client(DatabaseFixture.TenantA, PlatformInTenant), platform, HttpStatusCode.Forbidden)).ShouldBeEmpty();
    }

    private static bool SignedInUserOnly(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission == EndpointSecurityExtensions.SignedInUser;

    private HttpClient Client(Guid? tenant, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.For(tenant, subject));
        return client;
    }

    /// <summary>The calls answered with anything but the expected refusal: "METHOD url → status".</summary>
    private static async Task<List<string>> Refusals(HttpClient client, IEnumerable<(string Method, string Url, RouteEndpoint Endpoint)> calls, HttpStatusCode expected)
    {
        var unexpected = new List<string>();
        foreach (var (method, url, _) in calls)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);
            if (response.StatusCode != expected)
            {
                unexpected.Add($"{method} {url} → {(int)response.StatusCode}");
            }
        }

        return unexpected;
    }

    /// <summary>One concrete call per endpoint and method, with route values that satisfy its constraints.</summary>
    private static List<(string Method, string Url, RouteEndpoint Endpoint)> Calls(IEnumerable<RouteEndpoint> endpoints)
    {
        var calls = new List<(string, string, RouteEndpoint)>();
        foreach (var endpoint in endpoints)
        {
            var url = endpoint.RoutePattern.RawText!;
            foreach (var parameter in endpoint.RoutePattern.Parameters)
            {
                url = ParameterPattern(parameter.Name).Replace(url, Value(parameter), 1);
            }

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                calls.Add((method, url, endpoint));
            }
        }

        return calls;
    }

    private static Regex ParameterPattern(string name) => new($"\\{{\\*{{0,2}}{Regex.Escape(name)}(:[^}}]*)?\\??\\}}");

    private static string Value(RoutePatternParameterPart parameter)
    {
        var policies = parameter.ParameterPolicies.Select(p => p.Content ?? "").ToList();
        if (policies.Contains("guid"))
        {
            return Guid.CreateVersion7().ToString();
        }

        if (policies.Contains("int") || policies.Contains("apiVersion"))
        {
            return "1";
        }

        // regex(^(a|b|c)$): the first alternative.
        return policies.FirstOrDefault(p => p.StartsWith("regex(", StringComparison.Ordinal)) is { } regex && Alternative().Match(regex) is { Success: true } match
            ? match.Groups[1].Value
            : "X";
    }

    [GeneratedRegex(@"\(\^?\(?([A-Za-z0-9_-]+)")]
    private static partial Regex Alternative();
}
