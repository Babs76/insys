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
using Insys.Billing.Contracts;
using Insys.Billing.Infrastructure;
using Insys.BuildingBlocks.Application.Context;
using Insys.Finance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Insys.IntegrationTests;

/// <summary>
/// GAP-044, ADR-025: business dates are the tenant's. At 23:30 UTC on 31 October it is 01:30 on 1 November in
/// Johannesburg: October has ended for a South African tenant, while a tenant on UTC is still on the 31st.
/// </summary>
public sealed class BusinessDateTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly From = new(2026, 1, 1);
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database, time: new FakeTimeProvider(Instant));
        var db = fixture.Database;
        await db.SeedStatic(DatabaseFixture.TenantA, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-A", From,
            ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        await db.SeedStatic(DatabaseFixture.TenantB, "tenant.cfg_locale_defaults", "EFFECTIVE", "LOCALE-B", From,
            ("tenant", "B"), ("base_currency", "ZAR"), ("languages", new[] { "en" }), ("default_language", "en"), ("time_zone", "UTC"), ("country", "ZA"));
        await db.SeedBillingAndPayments(DatabaseFixture.TenantA, From);
        foreach (var account in new[] { "1100", "4000" })
        {
            await db.SeedStatic(DatabaseFixture.TenantA, "finance.mst_chart_of_accounts", "ACTIVE", $"ACC-{account}", From,
                ("account_code", account), ("name", $"Account {account}"), ("type", "ASSET"), ("gl_account", "GL" + account), ("status", "ACTIVE"));
        }

        foreach (var month in new[] { new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1) })
        {
            await db.SeedStatic(DatabaseFixture.TenantA, "finance.cfg_accounting_calendar_and_periods", "EFFECTIVE", $"CAL-{month:yyyy-MM}", From,
                ("period_code", $"{month:yyyy-MM}"), ("start_date", month), ("end_date", month.AddMonths(1).AddDays(-1)));
        }

        await db.SeedStatic(DatabaseFixture.TenantA, "finance.cfg_posting_rules", "EFFECTIVE", "PR-PREM", From,
            ("rule_code", "PR-PREM"), ("transaction_type", TransactionTypes.PremiumDue), ("debit_account", "1100"), ("credit_account", "4000"));
        await db.UserWithPermissions(DatabaseFixture.TenantA, "date-clerk",
        [
            BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate,
            FinancePermissions.PeriodRead, FinancePermissions.PeriodUpdate, FinancePermissions.JournalRead,
        ]);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "date-clerk");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task BusinessDate_IsTheTenantsLocalDate_WhileTimestampsStayInUtc_ADR025()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(DatabaseFixture.TenantA, "test");
        var businessDate = scope.ServiceProvider.GetRequiredService<IBusinessDate>();

        businessDate.Today.ShouldBe(new DateOnly(2026, 11, 1));
        businessDate.TodayFor(DatabaseFixture.TenantB).ShouldBe(new DateOnly(2026, 10, 31), "a tenant on UTC is still on the 31st");
        scope.ServiceProvider.GetRequiredService<IClock>().UtcNow.ShouldBe(Instant);
        scope.ServiceProvider.GetRequiredService<IClock>().UtcToday.ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public async Task AtOneThirtyLocal_TheDebitOrderDueTodayIsInvoiced_AndPostsToTheNewPeriod_AfterOctoberIsClosed_GAP044()
    {
        // October is closed for postings (soft close) once it has ended locally; November is open.
        foreach (var (period, action) in new[] { ("2026-10", "open"), ("2026-11", "open"), ("2026-10", "soft-close") })
        {
            var response = await _clerk.PostAsync($"/api/v1/finance/periods/{period}/{action}", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{period} {action}: {await response.Content.ReadAsStringAsync()}");
        }

        // A monthly debit order starting today (1 November locally).
        var account = await _clerk.Send(HttpMethod.Post, "/api/v1/billing/accounts/", new { payerPartyId = Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" },
            idempotent: true);
        account.StatusCode.ShouldBe(HttpStatusCode.Created, await account.Content.ReadAsStringAsync());
        var accountId = (await account.Json()).GetProperty("id").GetGuid();
        var policy = Guid.CreateVersion7();
        (await _clerk.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{accountId}/schedules", new
        {
            sourceDomain = "GENERAL", policyRef = policy, productVersionRef = "TEST v1", frequency = "MONTHLY", startDate = new DateOnly(2026, 11, 1),
            endDate = new DateOnly(2027, 10, 31), instalmentAmount = 120m,
        }, idempotent: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The daily billing job runs on the tenant's date: the instalment is due today and is invoiced.
        await _factory.Services.GetRequiredService<BillingJobs>().Run(CancellationToken.None);
        (await fixture.Database.ScalarAsOwner<DateOnly>("SELECT due_date FROM billing.invoice WHERE policy_ref = @p", ("p", policy))).ShouldBe(new DateOnly(2026, 11, 1));

        // Posted in November, not into the closed October.
        await _factory.DrainOutbox();
        var invoice = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM billing.invoice WHERE policy_ref = @p", ("p", policy));
        var journals = await (await _clerk.GetAsync($"/api/v1/finance/journals?businessRefType=INVOICE&businessRefId={invoice}")).Json();
        journals.EnumerateArray().Single().GetProperty("periodCode").GetString().ShouldBe("2026-11");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM finance.posting_exception WHERE tenant_id = @t", ("t", DatabaseFixture.TenantA))).ShouldBe(0);

        // The test-only runner (ADR-025): the daily jobs as of 1 December invoice December's instalment, for this tenant only.
        var run = await _clerk.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = new DateOnly(2026, 12, 1) });
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM billing.invoice WHERE policy_ref = @p AND due_date = '2026-12-01'", ("p", policy))).ShouldBe(1);
    }
}
