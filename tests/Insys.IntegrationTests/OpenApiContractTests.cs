using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Api;
using Insys.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The OpenAPI document is a contract. It must match docs/api/openapi-v1.json; to accept a change on purpose, run
/// the tests with INSYS_UPDATE_OPENAPI=1 and commit the updated file.
/// </summary>
public sealed class OpenApiContractTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly string SnapshotPath = Path.Combine(RepositoryPaths.Root, "docs", "api", "openapi-v1.json");
    private InsysApiFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task OpenApiDocument_MatchesTheCommittedSnapshot()
    {
        var current = Normalise(await _factory.CreateClient().GetStringAsync("/openapi/v1.json"));
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(SnapshotPath, current);
        }

        File.Exists(SnapshotPath).ShouldBeTrue("docs/api/openapi-v1.json is missing; run the tests once with INSYS_UPDATE_OPENAPI=1.");
        Normalise(await File.ReadAllTextAsync(SnapshotPath)).ShouldBe(current,
            "The API contract changed. If intended, run with INSYS_UPDATE_OPENAPI=1 and commit docs/api/openapi-v1.json.");
    }

    [Fact]
    public async Task BuildTimeOpenApiDocument_MatchesTheCommittedSnapshot()
    {
        var generated = Path.Combine(RepositoryPaths.Root, "src", "Host", "Insys.Api", "obj", "openapi", "openapi-v1.json");
        File.Exists(generated).ShouldBeTrue("Build the host first; it writes obj/openapi/openapi-v1.json.");

        Normalise(await File.ReadAllTextAsync(generated)).ShouldBe(Normalise(await File.ReadAllTextAsync(SnapshotPath)),
            "The build-time OpenAPI document differs from docs/api/openapi-v1.json.");
    }

    [Fact]
    public async Task OpenApiDocument_ListsVersionedPaths_WithBearerSecurity()
    {
        using var document = JsonDocument.Parse(await _factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        document.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ShouldAllBe(p => p.StartsWith("/api/v1/"));
        document.RootElement.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _).ShouldBeTrue();
    }

    [Fact]
    public void EveryApiEndpoint_DeclaresAPermissionPolicy_IAM007()
    {
        _ = _factory.CreateClient();
        var endpoints = _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .ToList();

        endpoints.ShouldNotBeEmpty();
        endpoints.Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null).Select(e => e.DisplayName).ShouldBeEmpty();
    }

    private static string Normalise(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}
