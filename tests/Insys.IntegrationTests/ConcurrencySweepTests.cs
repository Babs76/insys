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
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-031, BR-057, BR-064, BR-071 across every endpoint, not a sample: each change to an existing record in General, the
/// claims framework, Billing, Distribution, Life and Health that takes If-Match refuses a request without it with 428,
/// before anything runs, for a caller who holds its permission.
/// </summary>
public sealed class ConcurrencySweepTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Holder = "concurrency-sweep";

    [Fact]
    public async Task EveryChangeThatTakesIfMatch_RefusesARequestWithoutIt_428_BR031()
    {
        var factory = await fixture.Host(new Dictionary<string, string?> { ["RateLimiting:PermitsPerMinute"] = "1000000" });
        var calls = EndpointSecuritySweepTests.Calls(factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetMetadata<IfMatchRequiredMetadata>() is not null
                    && System.Text.RegularExpressions.Regex.IsMatch(e.RoutePattern.RawText ?? "", @"^/api/v\{[^}]+\}/(general|claims|billing|distribution|life|health)/(?!static/)")))
            .Where(c => c.Method != "GET").ToList();
        calls.Count.ShouldBeGreaterThan(80);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Holder,
            [.. calls.Select(c => c.Endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()!.Permission).Distinct()]);
        var client = factory.ClientFor(DatabaseFixture.TenantA, Holder);

        var answered = new List<string>();
        foreach (var (method, url, _) in calls)
        {
            var response = await client.Send(new HttpMethod(method), url, new { }, raw: true);
            if (response.StatusCode != HttpStatusCode.PreconditionRequired)
            {
                answered.Add($"{method} {url}: {(int)response.StatusCode} {await response.Code()}");
            }
        }

        answered.ShouldBeEmpty();
    }
}
