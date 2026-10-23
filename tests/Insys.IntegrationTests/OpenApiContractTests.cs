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
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The three OpenAPI documents (core, static-data, platform-operator) are contracts. Each must match its snapshot in
/// docs/api; to accept a change on purpose, run the tests with INSYS_UPDATE_OPENAPI=1 and commit the updated files.
/// </summary>
public sealed class OpenApiContractTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    public static TheoryData<string> Documents => new(ApiDocuments.All.Select(d => d.Name));

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The static data document has over 1 400 paths; building it takes longer than HttpClient's default 100 seconds on CI runners.</summary>
    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_MatchesItsCommittedSnapshot(string document)
    {
        var current = Normalise(await Client().GetStringAsync($"/openapi/{document}.json"));
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(Snapshot(document), current);
        }

        File.Exists(Snapshot(document)).ShouldBeTrue($"{Snapshot(document)} is missing; run the tests once with INSYS_UPDATE_OPENAPI=1.");
        Normalise(await File.ReadAllTextAsync(Snapshot(document))).ShouldBe(current,
            $"The {document} API contract changed. If intended, run with INSYS_UPDATE_OPENAPI=1 and commit {Snapshot(document)}.");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task BuildTimeOpenApiDocument_MatchesItsCommittedSnapshot(string document)
    {
        var generated = Path.Combine(RepositoryPaths.Root, "src", "Host", "Insys.Api", "obj", "openapi", $"openapi_{document}.json");
        File.Exists(generated).ShouldBeTrue("Build the host first; it writes obj/openapi/openapi_{document}.json.");

        Normalise(await File.ReadAllTextAsync(generated)).ShouldBe(Normalise(await File.ReadAllTextAsync(Snapshot(document))),
            $"The build-time {document} document differs from its snapshot.");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task OpenApiDocument_ListsVersionedPaths_WithBearerSecurity(string document)
    {
        using var json = JsonDocument.Parse(await Client().GetStringAsync($"/openapi/{document}.json"));

        json.RootElement.GetProperty("paths").EnumerateObject().ShouldNotBeEmpty();
        json.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ShouldAllBe(p => p.StartsWith("/api/v1/"));
        json.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task CoreDocument_HasNoStaticDataOperations()
    {
        using var core = JsonDocument.Parse(await Client().GetStringAsync("/openapi/core.json"));

        core.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name)
            .ShouldAllBe(p => !p.Contains("/static/") && !p.Contains("/static-data/") && !p.StartsWith("/api/v1/tenancy/"));
    }

    [Fact]
    public async Task EveryEndpoint_IsInExactlyOneDocument()
    {
        var client = Client();
        var operations = 0;
        foreach (var (name, _, _) in ApiDocuments.All)
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync($"/openapi/{name}.json"));
            operations += json.RootElement.GetProperty("paths").EnumerateObject().Sum(p => p.Value.EnumerateObject().Count(o => o.Name != "parameters"));
        }

        operations.ShouldBe(ApiEndpoints().Count);
    }

    [Fact]
    public void EveryApiEndpoint_DeclaresAPermissionPolicy_IAM007()
    {
        var endpoints = ApiEndpoints();

        endpoints.ShouldNotBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null).Select(e => e.DisplayName).ShouldBeEmpty();
    }

    private List<RouteEndpoint> ApiEndpoints()
    {
        _ = _factory.CreateClient();
        return _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .ToList();
    }

    private static string Snapshot(string document) => Path.Combine(RepositoryPaths.Root, "docs", "api", $"openapi-{document}.json");

    private static string Normalise(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
