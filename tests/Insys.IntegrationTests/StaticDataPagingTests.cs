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
using Insys.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-087: paging a static data list returns every in-force code exactly once. The cursor, the database's order and the
/// overlay's sort must agree; under the database's own collation (en_US) underscores are ignored, so a code on a page
/// boundary used to be skipped (SD_BENEFIT_MANAGEMENT_MAINTAIN in the function catalogue).
/// </summary>
public sealed class StaticDataPagingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Policies = "/api/v1/identity/static/authentication-and-session-policies";
    private const string Functions = "/api/v1/identity/static/function-catalogue";
    private static readonly DateOnly From = new(2020, 1, 1);
    private static readonly DateOnly AsAt = new(2026, 10, 27);

    // Codes whose order differs between en_US (PGA, PG_A, PGB, PG_B, ...) and bytewise (PGA, PGB, ..., PG_A, PG_B, ...).
    private static readonly string[] TenantCodes = ["BR087_PGA", "BR087_PG_A", "BR087_PGB", "BR087_PG_B", "BR087_PGC", "BR087_PG_C", "BR087_PG_CA"];
    private static readonly string[] GlobalCodes = ["BR087_PGB", "BR087_PGBA", "BR087_PG_BA", "BR087_PGD", "BR087_PG_D"];

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "paging-a", StaticDataPermissions.Read);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task PagingAStaticDataList_ReturnsEveryCodeExactlyOnce_WhenCodesStraddleAPageBoundary_BR087(int pageSize)
    {
        // The defect only shows where the database's collation ignores underscores, as the postgres:18 image's en_US does.
        (await fixture.Database.ScalarAsOwner<bool>("SELECT 'PG_A' < 'PGB'")).ShouldBeTrue("the test database must use a collation that ignores underscores");
        foreach (var code in TenantCodes)
        {
            await fixture.Database.SeedStaticFilled(DatabaseFixture.TenantA, "iam.cfg_authentication_and_session_policies", "PUBLISHED", code, From,
                ("policy_code", "BR087"));
        }

        foreach (var code in GlobalCodes)
        {
            await fixture.Database.SeedStaticFilled(null, "iam.cfg_authentication_and_session_policies", "PUBLISHED", code, From, ("policy_code", "BR087"));
        }

        var codes = await PageThrough(Policies, pageSize);

        codes.ShouldBe(codes.Order(StringComparer.Ordinal).ToList(), "pages come in bytewise code order");
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count, "no code is returned twice");
        var seeded = codes.Where(c => c.StartsWith("BR087_", StringComparison.Ordinal)).ToList();
        seeded.ShouldBe(TenantCodes.Union(GlobalCodes).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task PagingTheFunctionCatalogue_At200APage_ReturnsEveryFunctionExactlyOnce_BR087()
    {
        var expected = await fixture.Database.ScalarAsOwner<string[]>("""
            SELECT array_agg(DISTINCT code) FROM iam.ref_function_catalogue
            WHERE record_status IN ('ACTIVE', 'DEPRECATED') AND effective_from <= @d AND (effective_to IS NULL OR effective_to > @d)
            """, ("d", AsAt));
        expected.ShouldNotBeNull();
        expected.Length.ShouldBeGreaterThan(400, "the catalogue spans several pages");
        expected.ShouldContain("SD_BENEFIT_MANAGEMENT_MAINTAIN");

        var codes = await PageThrough(Functions, 200);

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count, "no function is returned twice");
        codes.ShouldBe(expected.Order(StringComparer.Ordinal).ToList());
    }

    private async Task<List<string>> PageThrough(string list, int pageSize)
    {
        var reader = _factory.ClientFor(DatabaseFixture.TenantA, "paging-a");
        var codes = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var response = await reader.GetAsync($"{list}?asAt={AsAt:yyyy-MM-dd}&limit={pageSize}{(cursor is null ? "" : $"&cursor={cursor}")}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Json();
            codes.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()!));
            if (body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
            {
                return codes;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        throw new InvalidOperationException("The list did not end.");
    }
}
