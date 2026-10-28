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
using Insys.General.Contracts;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-032: a General claim's recoveries are listed, every one once and only that claim's, by a cursor; a claim outside the
/// caller's scope (F103) is not found, and nor is one of its recoveries read by ID.
/// </summary>
public sealed class ClaimRecoveriesTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid T = DatabaseFixture.TenantA;
    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(T, "br032-reader", GeneralPermissions.ClaimRead);
        var scoped = await fixture.Database.UserWithPermissions(T, "br032-home-only", GeneralPermissions.ClaimRead);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['BR032-HOME'] WHERE user_id = @u", ("u", scoped));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AClaimsRecoveries_AreListedOnceEach_AndOnlyWithinScope_BR032()
    {
        var rows = new RowFiller(fixture.Database, DateTimeOffset.UtcNow);
        var claim = await rows.Insert(T, "general.claim", ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"), ("product_code", "BR032-MOTOR"));
        var other = await rows.Insert(T, "general.claim", ("status", "REGISTERED"), ("coverage_code", "OWN_DAMAGE"), ("product_code", "BR032-MOTOR"));
        var recoveries = new List<Guid>();
        foreach (var type in new[] { "SALVAGE", "THIRD_PARTY", "SUBROGATION" })
        {
            recoveries.Add(await Recovery(rows, claim, type));
        }

        var elsewhere = await Recovery(rows, other, "SALVAGE");
        var reader = _factory.ClientFor(T, "br032-reader");

        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var response = await reader.GetAsync($"/api/v1/general/claims/{claim}/recoveries?limit=2{(cursor is null ? "" : $"&cursor={cursor}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Json();
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
            if (body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
            {
                break;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        seen.ShouldBe([.. recoveries.Order()], "each of the claim's recoveries once, not another claim's");
        seen.ShouldNotContain(elsewhere);
        (await reader.GetAsync($"/api/v1/general/claims/{Guid.CreateVersion7()}/recoveries")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Outside the caller's product scope: neither the list nor a recovery by ID.
        var scoped = _factory.ClientFor(T, "br032-home-only");
        (await scoped.GetAsync($"/api/v1/general/claims/{claim}/recoveries")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await scoped.GetAsync($"/api/v1/general/recoveries/{recoveries[0]}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "read through its claim's scope");
        (await reader.GetAsync($"/api/v1/general/recoveries/{recoveries[0]}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<Guid> Recovery(RowFiller rows, Guid claim, string type) =>
        rows.Insert(T, "general.recovery_case", ("claim_id", claim), ("recovery_type", type), ("expected_amount", 1000m), ("recovered_amount", 0m), ("status", "OPEN"));
}
