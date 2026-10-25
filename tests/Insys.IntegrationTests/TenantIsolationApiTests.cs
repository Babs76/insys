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
using System.Text.Json;
using Insys.Integration.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// Tenant isolation through the API for the Phase 0 sample endpoint, Integration › dead letters (SRS §27.4, TEN-002,
/// TEN-004, IAM-007). The host connects as the non-owner role, so row-level security is the last line of defence.
/// </summary>
public sealed class TenantIsolationApiTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Operator = "operator-a";
    private const string Reader = "reader-a";
    private const string Nobody = "nobody-a";
    private InsysApiFactory _factory = null!;
    private Guid _operatorUserId;
    private readonly List<Guid> _tenantA = [];
    private Guid _tenantBRow;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        _operatorUserId = await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Operator,
            IntegrationPermissions.DeadLetterRead, IntegrationPermissions.DeadLetterUpdate);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Reader, IntegrationPermissions.DeadLetterRead);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, Nobody);
        for (var i = 0; i < 3; i++)
        {
            _tenantA.Add(await SeedDeadLetter(DatabaseFixture.TenantA));
        }

        _tenantBRow = await SeedDeadLetter(DatabaseFixture.TenantB);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task NoToken_Is401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/integration/dead-letters");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Code(response)).ShouldBe("UNAUTHENTICATED");
    }

    [Fact]
    public async Task TokenFromAnotherIssuer_Is401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.");

        (await client.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenWithoutTenant_Is403_AndReturnsNoData_TEN002()
    {
        var response = await _factory.ClientFor(null, Operator).GetAsync("/api/v1/integration/dead-letters");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Code(response)).ShouldBe("TENANT_CONTEXT_MISSING");
    }

    [Fact]
    public async Task DeniedRequest_IsAudited_TEN005_IAM006()
    {
        (await _factory.ClientFor(DatabaseFixture.TenantA, Nobody).GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND operation = 'AccessDenied' AND outcome = 'DENIED' AND target_id LIKE 'GET /api/v1/integration/dead-letters%'
            """, ("t", DatabaseFixture.TenantA))).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PermissionClaimInTheToken_IsIgnored_OQ014()
    {
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = TestTokens.Issuer,
            Audience = TestTokens.Audience,
            Subject = new System.Security.Claims.ClaimsIdentity([new("sub", Nobody), new("tenant_id", DatabaseFixture.TenantA.ToString()),
                new("permission", IntegrationPermissions.DeadLetterRead)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(TestTokens.SigningKey)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await client.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TokenWithoutPermission_Is403_IAM007()
    {
        var response = await _factory.ClientFor(DatabaseFixture.TenantA, Nobody).GetAsync("/api/v1/integration/dead-letters");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // ADR-026 §2: the user's roles grant no function for the operation, which is checked before its permission.
        (await Code(response)).ShouldBe("FUNCTION_NOT_GRANTED");
    }

    [Fact]
    public async Task ReadPermission_DoesNotGrantReplay_PrivilegeEscalation()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Reader);

        var response = await PostWithIfMatch(client, $"/api/v1/integration/dead-letters/{_tenantA[0]}/replay", "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersTenant_TEN004()
    {
        var page = await List(_factory.ClientFor(DatabaseFixture.TenantA, Operator), "/api/v1/integration/dead-letters?limit=200");

        page.ShouldContain(_tenantA[0]);
        page.ShouldNotContain(_tenantBRow);
    }

    [Theory]
    [InlineData("?tenant_id={B}")]
    [InlineData("?tenantId={B}")]
    public async Task List_IgnoresTenantInQueryString_TEN002(string query)
    {
        var page = await List(_factory.ClientFor(DatabaseFixture.TenantA, Operator),
            "/api/v1/integration/dead-letters" + query.Replace("{B}", DatabaseFixture.TenantB.ToString()));

        page.ShouldNotContain(_tenantBRow);
    }

    [Fact]
    public async Task List_IgnoresTenantHeaders_TEN002()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Operator);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", DatabaseFixture.TenantB.ToString());
        client.DefaultRequestHeaders.Add("tenant_id", DatabaseFixture.TenantB.ToString());

        (await List(client, "/api/v1/integration/dead-letters")).ShouldNotContain(_tenantBRow);
    }

    [Fact]
    public async Task List_TokenWithoutTenant_ButTenantHeader_IsStill403_TEN002()
    {
        var client = _factory.ClientFor(null, Operator);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", DatabaseFixture.TenantA.ToString());

        (await client.GetAsync("/api/v1/integration/dead-letters")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_AnotherTenantsRecord_Is404_TEN004()
    {
        var response = await _factory.ClientFor(DatabaseFixture.TenantA, Operator)
            .GetAsync($"/api/v1/integration/dead-letters/{_tenantBRow}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_AnotherTenantsRecord_Is404_AndLeavesItUnchanged_TEN004()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Operator);

        var response = await PostWithIfMatch(client, $"/api/v1/integration/dead-letters/{_tenantBRow}/discard", "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.Database.ScalarAsOwner<string>("SELECT coalesce(resolution, 'NONE') FROM integration.dead_letter_message WHERE id = @id", ("id", _tenantBRow)))
            .ShouldBe("NONE");
    }

    [Fact]
    public async Task Get_ReturnsETag_AndList_PagesWithCursor()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Operator);

        var get = await client.GetAsync($"/api/v1/integration/dead-letters/{_tenantA[0]}");
        get.Headers.ETag!.Tag.ShouldBe("\"1\"");

        var firstPage = await client.GetFromJsonAsync<JsonElement>("/api/v1/integration/dead-letters?limit=1");
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNull();
        var secondPage = await client.GetFromJsonAsync<JsonElement>($"/api/v1/integration/dead-letters?limit=1&cursor={cursor}");
        secondPage.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(firstPage.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Discard_RequiresIfMatch_ChecksVersion_ThenRejectsASecondTransition_DBA011()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Operator);
        var url = $"/api/v1/integration/dead-letters/{_tenantA[1]}/discard";

        (await client.PostAsync(url, null)).StatusCode.ShouldBe((HttpStatusCode)428);
        (await PostWithIfMatch(client, url, "\"7\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        var discarded = await PostWithIfMatch(client, url, "\"1\"");
        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        discarded.Headers.ETag!.Tag.ShouldBe("\"2\"");

        var again = await PostWithIfMatch(client, $"/api/v1/integration/dead-letters/{_tenantA[1]}/replay", "\"2\"");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(again)).ShouldBe("INVALID_STATE_TRANSITION");
    }

    [Fact]
    public async Task Replay_ByOperator_ResolvesAndAudits_INT006_IAM006()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, Operator);

        var response = await PostWithIfMatch(client, $"/api/v1/integration/dead-letters/{_tenantA[2]}/replay", "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.Database.ScalarAsOwner<string>("SELECT resolution FROM integration.dead_letter_message WHERE id = @id", ("id", _tenantA[2])))
            .ShouldBe("REPLAYED");
        (await fixture.Database.ScalarAsOwner<Guid>("SELECT resolved_by FROM integration.dead_letter_message WHERE id = @id", ("id", _tenantA[2])))
            .ShouldBe(_operatorUserId);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("""
            SELECT count(*) FROM audit.audit_event
            WHERE tenant_id = @t AND target_id = @id AND operation = 'ReplayMessage' AND outcome = 'SUCCESS' AND actor_id = @actor
            """, ("t", DatabaseFixture.TenantA), ("id", _tenantA[2].ToString()), ("actor", _operatorUserId.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task Replay_ByASubjectWithNoIamUser_Is403_INT006()
    {
        var client = _factory.ClientFor(DatabaseFixture.TenantA, "not-provisioned");

        var response = await PostWithIfMatch(client, $"/api/v1/integration/dead-letters/{_tenantA[0]}/replay", "\"1\"");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Errors_AreProblemDetails_WithoutInternalDetail()
    {
        var response = await _factory.ClientFor(DatabaseFixture.TenantA, Operator)
            .GetAsync($"/api/v1/integration/dead-letters/{Guid.CreateVersion7()}");

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"code\":\"NOT_FOUND\"");
        body.ShouldNotContain("Npgsql");
        body.ShouldNotContain("   at ");
    }

    private async Task<Guid> SeedDeadLetter(Guid tenant)
    {
        var id = Guid.CreateVersion7();
        var envelope = JsonSerializer.Serialize(new
        {
            eventId = Guid.CreateVersion7(), tenantId = tenant, eventType = "test.Nothing", eventVersion = 1, occurredAt = DateTimeOffset.UtcNow,
            aggregateType = "None", aggregateId = Guid.CreateVersion7(), correlationId = Guid.CreateVersion7(), causationId = (Guid?)null, payload = new { },
        });
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO integration.dead_letter_message
              (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, source, message_id, payload, error, first_failed_at, attempts)
            VALUES (@t, @id, 1, now(), 'seed', now(), 'seed', 'outbox:test', @id::text, @payload::jsonb, 'System.TimeoutException', now(), 10)
            """, ("t", tenant), ("id", id), ("payload", envelope));
        return id;
    }

    private static async Task<List<Guid>> List(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private static Task<HttpResponseMessage> PostWithIfMatch(HttpClient client, string url, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
