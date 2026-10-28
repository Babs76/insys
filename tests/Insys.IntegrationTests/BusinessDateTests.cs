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
        journals.GetProperty("items").EnumerateArray().Single().GetProperty("periodCode").GetString().ShouldBe("2026-11");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM finance.posting_exception WHERE tenant_id = @t", ("t", DatabaseFixture.TenantA))).ShouldBe(0);

        // The test-only runner (ADR-025): the daily jobs as of 1 December invoice December's instalment, for this tenant only.
        var run = await _clerk.PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = new DateOnly(2026, 12, 1) });
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM billing.invoice WHERE policy_ref = @p AND due_date = '2026-12-01'", ("p", policy))).ShouldBe(1);
    }

    /// <summary>
    /// Wave 1 batch 4, ADR-025: at 23:30 UTC on 31 October, a policy issued now counts in Analytics on 1 November for the
    /// Johannesburg tenant and on 31 October for the tenant on UTC. A fact logged before this release by the UTC day is
    /// dated again by the tenant's business date when the projections are rebuilt.
    /// </summary>
    [Fact]
    public async Task AtHalfPastElevenUtc_AnalyticsCountsOnEachTenantsBusinessDate_AndARebuildCorrectsAFactDatedByTheUtcDay_ADR025()
    {
        var bus = _factory.Services.GetRequiredService<Insys.BuildingBlocks.Application.Messaging.IEventBus>();
        foreach (var (tenant, product) in new[] { (DatabaseFixture.TenantA, "BD-JHB"), (DatabaseFixture.TenantB, "BD-UTC") })
        {
            var policy = Guid.CreateVersion7();
            var party = Guid.CreateVersion7();
            var issued = new Insys.General.Contracts.GeneralPolicyIssued(policy, $"GP-{policy.ToString("N")[^8..]}", product, 1, party, party, Guid.CreateVersion7(),
                "MONTHLY", "DEBIT_ORDER", "ZAR", 1_200m, new DateOnly(2026, 11, 1), new DateOnly(2027, 10, 31), null);
            await bus.Deliver(Envelope(tenant, issued, "general.GeneralPolicyIssued", "GeneralPolicy", policy), CancellationToken.None);
        }

        (await DateKey(DatabaseFixture.TenantA, "BD-JHB")).ShouldBe(new DateOnly(2026, 11, 1), "01:30 on 1 November in Johannesburg");
        (await DateKey(DatabaseFixture.TenantB, "BD-UTC")).ShouldBe(new DateOnly(2026, 10, 31), "still the 31st on UTC");

        // As the previous release logged it: dated by the UTC day.
        var old = new Insys.Analytics.Application.ProjectionEntry(Insys.Analytics.Application.ProjectionEntry.WrittenPremium, new DateOnly(2026, 10, 31), "BD-OLD",
            "DIRECT", "ALL", 500m, 1, "ZAR");
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO analytics.event_log (tenant_id, event_id, event_type, occurred_at, received_at, payload)
            VALUES (@t, gen_random_uuid(), 'general.GeneralPolicyIssued', @at, now(), @payload::jsonb)
            """, ("t", DatabaseFixture.TenantA), ("at", Instant), ("payload", System.Text.Json.JsonSerializer.Serialize(old, System.Text.Json.JsonSerializerOptions.Web)));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ExecutionScope>().InitialiseForService(DatabaseFixture.TenantA, "test");
            (await scope.ServiceProvider.GetRequiredService<Insys.BuildingBlocks.Application.Messaging.IDispatcher>()
                .Send(new Insys.Analytics.Application.RebuildProjections(), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        }

        (await DateKey(DatabaseFixture.TenantA, "BD-OLD")).ShouldBe(new DateOnly(2026, 11, 1), "rebuilt by the tenant's business date");
        (await DateKey(DatabaseFixture.TenantA, "BD-JHB")).ShouldBe(new DateOnly(2026, 11, 1));
    }

    /// <summary>
    /// Wave 2 batch 1: the release runs the rebuild by business date itself, once per tenant, recorded in
    /// analytics.release_task so it never runs twice.
    /// </summary>
    [Fact]
    public async Task TheReleaseRebuildsEachTenantsProjectionsOnce_AndRecordsIt()
    {
        var old = new Insys.Analytics.Application.ProjectionEntry(Insys.Analytics.Application.ProjectionEntry.WrittenPremium, new DateOnly(2026, 10, 31), "BD-REL",
            "DIRECT", "ALL", 700m, 1, "ZAR");
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO analytics.event_log (tenant_id, event_id, event_type, occurred_at, received_at, payload)
            VALUES (@t, gen_random_uuid(), 'general.GeneralPolicyIssued', @at, now(), @payload::jsonb)
            """, ("t", DatabaseFixture.TenantB), ("at", Instant.AddHours(2)), ("payload", System.Text.Json.JsonSerializer.Serialize(old, System.Text.Json.JsonSerializerOptions.Web)));
        var tasks = _factory.Services.GetRequiredService<Insys.Analytics.Infrastructure.AnalyticsReleaseTasks>();

        (await tasks.RunFor(DatabaseFixture.TenantB, CancellationToken.None)).ShouldNotBeNull();
        (await DateKey(DatabaseFixture.TenantB, "BD-REL")).ShouldBe(new DateOnly(2026, 11, 1), "01:30 UTC on 1 November is 1 November on UTC too");
        (await tasks.RunFor(DatabaseFixture.TenantB, CancellationToken.None)).ShouldBeNull("it is recorded, so it never runs twice");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM analytics.release_task WHERE tenant_id = @t AND task_code = 'ANALYTICS_REBUILD_BY_BUSINESS_DATE'",
            ("t", DatabaseFixture.TenantB))).ShouldBe(1L);
    }

    private Task<DateOnly> DateKey(Guid tenant, string product) =>
        fixture.Database.ScalarAsOwner<DateOnly>("SELECT date_key FROM analytics.fact_written_premium WHERE tenant_id = @t AND product_code = @p", ("t", tenant), ("p", product));

    private static Insys.BuildingBlocks.Contracts.EventEnvelope Envelope<T>(Guid tenant, T integrationEvent, string type, string aggregateType, Guid aggregateId) =>
        new(Guid.CreateVersion7(), tenant, type, 1, Instant, aggregateType, aggregateId, Guid.CreateVersion7(), null,
            System.Text.Json.JsonSerializer.SerializeToElement(integrationEvent, System.Text.Json.JsonSerializerOptions.Web));
}
