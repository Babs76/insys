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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// BR-007 (isolation): a platform-maintained static data row that names a tenant (a `tenant` column) is that tenant's
/// to read. Every static data table with a `tenant` column is found from the schema, so a table added later is covered:
/// rows naming two tenants are seeded, and tenant A sees its own and never B's, by list, by code and by versions; the
/// platform operator still sees both through `…/global`.
/// </summary>
public sealed class TenantNamedRowsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly From = TestClock.Today.AddDays(-30);

    /// <summary>Columns a table's CHECK constraints need, where the default fill would not pass them.</summary>
    private static readonly Dictionary<string, (string Column, object Value)[]> Fields = new(StringComparer.Ordinal)
    {
        ["tenant.cfg_feature_entitlements"] = [("feature", "LIFE")],
        ["tenant.cfg_isolation_profile_assignment"] = [("profile", "A")],
    };

    private InsysApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await fixture.Host();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ARowNamingATenant_IsReadOnlyByThatTenant_InEveryStaticTable_BR007()
    {
        var tables = (await fixture.Database.ScalarAsOwner<string[]>("""
            SELECT array_agg(c.table_schema || '.' || c.table_name ORDER BY c.table_schema, c.table_name) FROM information_schema.columns c
            WHERE c.column_name = 'tenant'
              AND EXISTS (SELECT 1 FROM information_schema.columns s WHERE s.table_schema = c.table_schema AND s.table_name = c.table_name AND s.column_name = 'record_status')
              -- Platform rows (scope G or GT: tenant_id may be empty); a tenant's own rows (scope T) are isolated by tenant_id already.
              AND EXISTS (SELECT 1 FROM information_schema.columns g WHERE g.table_schema = c.table_schema AND g.table_name = c.table_name
                          AND g.column_name = 'tenant_id' AND g.is_nullable = 'YES')
            """))!;
        tables.ShouldContain("tenant.cfg_feature_entitlements");

        var descriptors = _factory.Services.GetServices<ModuleStaticDataTables>().SelectMany(m => m.Descriptors.Select(d => (m.ModuleRoute, Descriptor: d))).ToList();
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "named-rows-reader", "static_data.read");
        await fixture.Database.UserWithPermissions(TestTenants.Operator, "named-rows-operator", "static_data.read");
        var reader = _factory.ClientFor(DatabaseFixture.TenantA, "named-rows-reader");
        var operatorClient = _factory.ClientFor(TestTenants.Operator, "named-rows-operator");
        var leaks = new List<string>();

        foreach (var table in tables)
        {
            var (moduleRoute, descriptor) = descriptors.Single(d => $"{d.Descriptor.Schema}.{d.Descriptor.Table}" == table);
            var url = $"/api/v1/{moduleRoute}/static/{descriptor.EntityRoute}";
            var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            var own = $"NR-A-{suffix}";
            var other = $"NR-B-{suffix}";
            var extra = Fields.GetValueOrDefault(table, []);
            // In force: EFFECTIVE in the configuration lifecycle, ACTIVE in the reference one.
            var status = await fixture.Database.ScalarAsOwner<bool>("SELECT pg_get_constraintdef(oid) LIKE '%EFFECTIVE%' FROM pg_constraint WHERE conname = @n",
                ("n", $"{descriptor.Table}_stat")) ? "EFFECTIVE" : "ACTIVE";
            await fixture.Database.SeedStaticFilled(null, table, status, own, From, [("tenant", "TENA"), .. extra]);
            await fixture.Database.SeedStaticFilled(null, table, status, other, From, [("tenant", "TENB"), .. extra]);

            var listed = await (await reader.GetAsync($"{url}?limit=200")).Json();
            var codes = listed.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("code").GetString()).ToHashSet(StringComparer.Ordinal);
            if (codes.Contains(other))
            {
                leaks.Add($"{table}: listed");
            }

            if (!codes.Contains(own))
            {
                leaks.Add($"{table}: its own row is not listed");
            }

            if ((await reader.GetAsync($"{url}/{other}")).StatusCode != HttpStatusCode.NotFound)
            {
                leaks.Add($"{table}: read by code");
            }

            var versions = await reader.GetAsync($"{url}/{other}/versions");
            if (versions.StatusCode == HttpStatusCode.OK && (await versions.Json()).EnumerateArray().Any())
            {
                leaks.Add($"{table}: versions listed");
            }

            var global = await operatorClient.GetAsync($"{url}/global/{other}/versions");
            if (global.StatusCode != HttpStatusCode.OK || !(await global.Json()).EnumerateArray().Any())
            {
                leaks.Add($"{table}: the operator cannot read it through /global ({(int)global.StatusCode})");
            }
        }

        leaks.ShouldBeEmpty();
    }
}
