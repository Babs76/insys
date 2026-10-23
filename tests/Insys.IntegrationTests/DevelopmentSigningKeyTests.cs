using System.Net;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// The HS256 development signing key is a local development and test convenience only (security fix, Phase 8): any
/// other environment refuses to start with it configured, and does not accept tokens signed with it.
/// </summary>
public sealed class DevelopmentSigningKeyTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Host_WithTheDevelopmentKeyConfigured_RefusesToStart_OutsideDevelopmentAndTesting(string environment)
    {
        await using var factory = new InsysApiFactory(fixture.Database, environment: environment);

        var refused = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        refused.Message.ShouldContain("Authentication:DevelopmentSigningKey");
        refused.Message.ShouldContain(environment);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task TokenSignedWithTheDevelopmentKey_IsRejected_OutsideDevelopmentAndTesting(string environment)
    {
        // Without the key the host starts; a token signed with it is still unauthenticated.
        await using var factory = new InsysApiFactory(fixture.Database, new Dictionary<string, string?> { ["Authentication:DevelopmentSigningKey"] = "" }, environment);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dev-key-user", "static_data.read");

        var response = await factory.ClientFor(DatabaseFixture.TenantA, "dev-key-user").GetAsync("/api/v1/static-data/static/currencies");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenSignedWithTheDevelopmentKey_IsAccepted_InTesting()
    {
        await using var factory = new InsysApiFactory(fixture.Database);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "dev-key-tester", "static_data.read");

        (await factory.ClientFor(DatabaseFixture.TenantA, "dev-key-tester").GetAsync("/api/v1/static-data/static/currencies")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
