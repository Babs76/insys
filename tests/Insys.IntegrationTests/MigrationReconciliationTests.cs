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
using Insys.BuildingBlocks.Application.Context;
using Insys.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Insys.IntegrationTests;

/// <summary>BR-128: a batch may hold several currencies, so a kind reconciles per currency, and an amount never goes without its currency.</summary>
public sealed class MigrationReconciliationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private const string Url = "/api/v1/integration/migration-batches/MIXED/reconciliation";

    private sealed class TwoCurrencies : IMigrationTotals
    {
        public Task<IReadOnlyList<MigrationTotal>> For(Guid tenantId, string batchCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MigrationTotal>>(batchCode != "MIXED" ? [] :
                [new("GENERAL_POLICY", 2, 1000m, "ZAR"), new("GENERAL_POLICY", 1, 1000m, "USD"), new("GENERAL_CLAIM", 1, null)]);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _host;

    private async Task<HttpClient> Client()
    {
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "migrator-br128", "migration_batch.reconcile");
        var host = _host ??= new InsysApiFactory(fixture.Database).WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMigrationTotals, TwoCurrencies>()));
        var client = host.CreateDefaultClient(new TestPreconditionsHandler());
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(DatabaseFixture.TenantA, "migrator-br128", DateTime.UtcNow));
        return client;
    }

    [Fact]
    public async Task EachCurrency_ReconcilesOnItsOwn_NeverAgainstASumAcrossThem()
    {
        var client = await Client();
        var answered = await client.Send(HttpMethod.Post, Url, new object[]
        {
            new { kind = "GENERAL_POLICY", count = 2L, amount = 1000m, currency = "ZAR" }, new { kind = "GENERAL_POLICY", count = 1L, amount = 1000m, currency = "usd" },
            new { kind = "GENERAL_CLAIM", count = 1L },
        });
        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
        var agrees = await answered.Json();
        agrees.GetProperty("reconciled").GetBoolean().ShouldBeTrue(agrees.ToString());
        agrees.GetProperty("lines").GetArrayLength().ShouldBe(3);

        // The right total, in one currency: both lines differ, though a sum across them would agree.
        var swapped = await (await client.Send(HttpMethod.Post, Url, new object[]
        {
            new { kind = "GENERAL_POLICY", count = 3L, amount = 2000m, currency = "ZAR" }, new { kind = "GENERAL_CLAIM", count = 1L },
        })).Json();
        swapped.GetProperty("reconciled").GetBoolean().ShouldBeFalse();
        swapped.GetProperty("lines").EnumerateArray().Where(l => !l.GetProperty("agrees").GetBoolean()).Select(l => l.GetProperty("currency").GetString()).ShouldBe(["USD", "ZAR"]);
    }

    [Fact]
    public async Task AnAmount_WithNoCurrency_IsRefused_AndSoIsTheSameTotalTwice()
    {
        var client = await Client();
        (await client.Send(HttpMethod.Post, Url, new object[] { new { kind = "GENERAL_POLICY", count = 2L, amount = 1000m } })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.Send(HttpMethod.Post, Url, new object[]
        {
            new { kind = "GENERAL_POLICY", count = 2L, amount = 1000m, currency = "ZAR" }, new { kind = "GENERAL_POLICY", count = 2L, amount = 1000m, currency = "zar" },
        })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}
