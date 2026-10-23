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

namespace Insys.IntegrationTests;

/// <summary>
/// Development CORS (GAP-002): the configured dev-server origin may call the API from a browser and read the headers
/// the front end uses; any other origin may not; and outside Development and Testing the host refuses to start with it.
/// </summary>
public sealed class CorsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string DevServer = "http://localhost:5173";

    [Fact]
    public async Task DevServerOrigin_IsAllowed_WithTheFrontEndsHeaders_AndOtherOriginsAreNot()
    {
        var factory = await fixture.Host(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = DevServer });
        var client = factory.CreateClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/identity/me");
        preflight.Headers.Add("Origin", DevServer);
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,if-match,content-type,accept-language");
        var allowed = await client.SendAsync(preflight);
        allowed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        allowed.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([DevServer]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Add("Origin", DevServer);
        var response = await client.SendAsync(request);
        response.Headers.GetValues("Access-Control-Expose-Headers").Single().Split(',').ShouldBe(DevelopmentCors.ExposedHeaders, ignoreOrder: true);

        using var other = new HttpRequestMessage(HttpMethod.Options, "/api/v1/identity/me");
        other.Headers.Add("Origin", "https://elsewhere.example.com");
        other.Headers.Add("Access-Control-Request-Method", "GET");
        (await client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Origins_OutsideDevelopmentAndTesting_StopTheHostFromStarting()
    {
        await using var factory = new InsysApiFactory(fixture.Database,
            new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = DevServer, ["Authentication:DevelopmentSigningKey"] = "" }, environment: "Staging");

        Should.Throw<InvalidOperationException>(() => factory.CreateClient()).Message.ShouldContain("Cors:AllowedOrigins");
    }
}
