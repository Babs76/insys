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
using System.Text.Json;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.StaticData.Contracts;
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-015: a change request leads straight to the version it creates (versionNumber and entityRoute), tenant and global
/// alike. BR-014: the governance settings carry an ETag, and their change takes If-Match (stale 412, missing 428).
/// </summary>
public sealed class StaticDataChangeRequestLinkTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly string[] All =
        [StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, StaticDataPermissions.Approve, StaticDataGovernancePermissions.ChangeRequestRead];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "link-maker-a", All);
        await fixture.Database.UserWithPermissions(DatabaseFixture.Operator, "link-op-maker", All);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ChangeRequest_GivesItsVersionNumberAndTableRoute_ThatReachTheSubmittedVersion_BR015()
    {
        var maker = _factory.ClientFor(DatabaseFixture.TenantA, "link-maker-a");
        const string table = "/api/v1/identity/static/roles-and-permission-sets";
        var code = "LINK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await maker.Send(HttpMethod.Post, table, new { code, roleCode = code, scopeProducts = new[] { "LIFE-TERM" }, changeReason = "Link test" },
            idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await maker.Send(HttpMethod.Post, $"{table}/{code}/versions/1/submit", ifMatch: created.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        var view = await ChangeRequest(maker, DatabaseFixture.TenantA, $"{code}@1");

        view.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        view.GetProperty("entityRoute").GetString().ShouldBe("identity/static/roles-and-permission-sets");
        await ShouldReachTheVersion(maker, view, "SUBMITTED");

        // The list carries them too.
        var page = await (await maker.GetAsync("/api/v1/static-data/change-requests?entityCode=" + view.GetProperty("entityCode").GetString() + "&limit=200")).Json();
        var listed = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == view.GetProperty("id").GetGuid());
        listed.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        listed.GetProperty("entityRoute").GetString().ShouldBe("identity/static/roles-and-permission-sets");
    }

    [Fact]
    public async Task GlobalChangeRequest_GivesTheGlobalRoute_ThatReachesTheVersion_BR015()
    {
        var maker = _factory.ClientFor(DatabaseFixture.Operator, "link-op-maker");
        const string table = "/api/v1/identity/static/authentication-and-session-policies";
        var code = "LINKG_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await maker.Send(HttpMethod.Post, $"{table}/global",
            new { code, policyCode = code, mfaRequiredRoles = Array.Empty<string>(), sessionTimeout = "PT30M", lockoutAttempts = 5, changeReason = "Link test" },
            idempotent: true);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await maker.Send(HttpMethod.Post, $"{table}/global/{code}/versions/1/submit", ifMatch: created.ETag())).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();

        var view = await ChangeRequest(maker, DatabaseFixture.Operator, $"G:{code}@1");

        view.GetProperty("versionNumber").GetInt32().ShouldBe(1);
        view.GetProperty("entityRoute").GetString().ShouldBe("identity/static/authentication-and-session-policies/global");
        await ShouldReachTheVersion(maker, view, "SUBMITTED");
    }

    [Fact]
    public async Task GovernanceSettings_CarryAnETag_AndTheirChangeTakesIfMatch_StaleGives412_BR014()
    {
        var tenant = Guid.CreateVersion7();
        await fixture.Database.SeedTenant(tenant, "G" + tenant.ToString("N")[^8..].ToUpperInvariant());
        await fixture.Database.UserWithPermissions(tenant, "governance-admin",
            [.. All, StaticDataGovernancePermissions.SettingRead, StaticDataGovernancePermissions.SettingUpdate]);
        var admin = _factory.ClientFor(tenant, "governance-admin");
        const string settings = "/api/v1/static-data/settings";

        var read = await admin.GetAsync(settings);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.ETag().ShouldBe("\"0\"");
        (await read.Json()).GetProperty("rowVersion").GetInt64().ShouldBe(0);

        var missing = await admin.Send(HttpMethod.Put, $"{settings}/tier2-maker-checker", new { tier2MakerChecker = true });
        missing.StatusCode.ShouldBe((HttpStatusCode)428);

        var changed = await admin.Send(HttpMethod.Put, $"{settings}/tier2-maker-checker", new { tier2MakerChecker = true }, ifMatch: read.ETag());
        changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        changed.ETag().ShouldBe("\"1\"");

        var reread = await admin.GetAsync(settings);
        reread.ETag().ShouldBe("\"1\"");

        var stale = await admin.Send(HttpMethod.Put, $"{settings}/tier2-maker-checker", new { tier2MakerChecker = false }, ifMatch: "\"0\"");
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await stale.Code()).ShouldBe("PRECONDITION_FAILED");
        (await (await admin.GetAsync(settings)).Json()).GetProperty("pendingTier2MakerChecker").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private async Task<JsonElement> ChangeRequest(HttpClient client, Guid tenant, string recordKey)
    {
        var id = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM sdm.change_request WHERE tenant_id = @t AND record_key = @k",
            ("t", tenant), ("k", recordKey));
        id.ShouldNotBe(Guid.Empty, "the change request is recorded once the outbox is drained");
        var response = await client.GetAsync($"/api/v1/static-data/change-requests/{id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Json();
    }

    /// <summary>GET /api/v1/{entityRoute}/{record.code}/versions holds the version numbered versionNumber, in the status given.</summary>
    private static async Task ShouldReachTheVersion(HttpClient client, JsonElement view, string status)
    {
        var code = view.GetProperty("record").GetProperty("code").GetString();
        var versions = await client.GetAsync($"/api/v1/{view.GetProperty("entityRoute").GetString()}/{code}/versions");
        versions.StatusCode.ShouldBe(HttpStatusCode.OK, await versions.Content.ReadAsStringAsync());
        var version = (await versions.Json()).EnumerateArray().Single(v => v.GetProperty("version").GetInt32() == view.GetProperty("versionNumber").GetInt32());
        version.GetProperty("recordStatus").GetString().ShouldBe(status);
        version.GetProperty("changeRequestId").GetGuid().ShouldBe(view.GetProperty("id").GetGuid());
    }
}
