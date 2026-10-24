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
using Insys.Billing.Contracts;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.Integration.Infrastructure.Parking;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Front-end request 46: the first daily run that finds a premium overdue opens the arrears case, even when the run on
/// its due date was missed; with no ageing bucket for the days overdue, the opening is parked (ADR-022) and done once a
/// bucket is published.
/// </summary>
public sealed class ArrearsAgeingTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Consumer = "billing.ArrearsOnOverdue.InvoiceOverdue";
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly Guid TenantB = DatabaseFixture.TenantB;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = await fixture.Host();
        await fixture.Database.SeedBillingAndPayments(Tenant, Today.AddDays(-60));
        await fixture.Database.UserWithPermissions(Tenant, "arrears-clerk", BillingPermissions.AccountCreate, BillingPermissions.AccountRead, BillingPermissions.AccountUpdate,
            BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate, "daily_job.run");
        _clerk = _factory.ClientFor(Tenant, "arrears-clerk");
        await fixture.Database.SeedBillingAndPayments(TenantB, Today.AddDays(-60));
        await fixture.Database.UserWithPermissions(TenantB, "arrears-clerk-b", BillingPermissions.AccountCreate, BillingPermissions.AccountRead,
            BillingPermissions.AccountUpdate, BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate, "daily_job.run");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task TheFirstRunAfterTheDueDate_OpensTheCase_AndWithoutABucketTheOpeningIsParked_Request46_ADR022()
    {
        // No bucket yet. The run on the due date was missed; the run the day after invoices the premium and finds it overdue.
        var (unbucketed, dueOn) = await Schedule(Today.AddDays(-5));
        await RunAsOf(dueOn.AddDays(1));
        (await InvoiceDueDate(unbucketed)).ShouldBe(dueOn, "a premium invoiced late is still due on its own date");
        (await InvoiceStatus(unbucketed)).ShouldBe("OVERDUE");
        (await OpenCases(unbucketed)).ShouldBe(0);
        (await Parked(unbucketed)).ShouldBe($"{PrerequisiteCodes.ArrearsAgeingBucketNotInForce} 1", "parked, naming the days no bucket covers; not skipped");

        // A bucket is published: the parked opening is retried and the case opens (5 days overdue by now).
        await fixture.Database.SeedStatic(Tenant, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "D1", Today.AddDays(-60), ("bucket_code", "D1"), ("days_from", 1),
            ("days_to", 9999), ("action", "REMINDER"));
        await RetryAfterPublishing();
        (await ParkedStatus(unbucketed)).ShouldBe("RESOLVED");
        (await OpenCases(unbucketed)).ShouldBe(1);

        // With the bucket in force, a single run the day after the due date opens the case at once, 1 day overdue.
        var (bucketed, alsoDueOn) = await Schedule(Today.AddDays(-3));
        await RunAsOf(alsoDueOn.AddDays(1));
        (await OpenCases(bucketed)).ShouldBe(1);
        (await fixture.Database.ScalarAsOwner<int>("SELECT days_overdue FROM billing.arrears_case WHERE billing_account_id = @a AND status = 'OPEN'", ("a", bucketed)))
            .ShouldBe(1);
        var account = await (await _clerk.GetAsync($"/api/v1/billing/accounts/{bucketed}")).Json();
        account.GetProperty("status").GetString().ShouldBe("InArrears");
    }

    [Fact]
    public async Task ACasePastItsLastBucket_StaysInItAndIsParkedOnce_ThenAgesOnWhenABucketCoversIt_Request46_ADR022()
    {
        // In tenant B, buckets cover only 1 to 3 days. The case opens at 1 day, then ages past the last bucket.
        var clerk = _factory.ClientFor(TenantB, "arrears-clerk-b");
        await fixture.Database.SeedStatic(TenantB, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B1", Today.AddDays(-60), ("bucket_code", "B1"), ("days_from", 1),
            ("days_to", 3), ("action", "REMINDER"));
        var (account, dueOn) = await Schedule(Today.AddDays(-10), clerk);
        await RunAsOf(dueOn.AddDays(1), clerk);
        (await Case(account)).ShouldBe(("B1", 1));

        await RunAsOf(dueOn.AddDays(5), clerk);
        (await Case(account)).ShouldBe(("B1", 5), "it stays in its bucket, with its true days overdue");
        await RunAsOf(dueOn.AddDays(6), clerk);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.parked_delivery WHERE consumer = @c AND payload ->> 'accountId' = @a",
            ("c", "billing.ArrearsOnOverdue.ArrearsAgeingBlocked"), ("a", account.ToString()))).ShouldBe(1L, "parked once, not once a day");
        (await fixture.Database.ScalarAsOwner<string>("""
            SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery WHERE consumer = @c AND payload ->> 'accountId' = @a AND status = 'PARKED'
            """, ("c", "billing.ArrearsOnOverdue.ArrearsAgeingBlocked"), ("a", account.ToString()))).ShouldBe($"{PrerequisiteCodes.ArrearsAgeingBucketNotInForce} 5");

        // A bucket for the later days is published: the parked delivery ages the case on (10 days overdue by now).
        await fixture.Database.SeedStatic(TenantB, "billing.cfg_arrears_ageing_buckets", "EFFECTIVE", "B2", Today.AddDays(-60), ("bucket_code", "B2"), ("days_from", 4),
            ("days_to", 9999), ("action", "LAPSE"));
        await RetryAfterPublishing(TenantB);
        (await Case(account)).ShouldBe(("B2", 10));
    }

    private async Task<(string? Bucket, int Days)> Case(Guid account) =>
        (await fixture.Database.ScalarAsOwner<string>("SELECT bucket_code FROM billing.arrears_case WHERE billing_account_id = @a AND status = 'OPEN'", ("a", account)),
         await fixture.Database.ScalarAsOwner<int>("SELECT days_overdue FROM billing.arrears_case WHERE billing_account_id = @a AND status = 'OPEN'", ("a", account)));

    private async Task<(Guid Account, DateOnly DueOn)> Schedule(DateOnly start, HttpClient? client = null)
    {
        var clerk = client ?? _clerk;
        var account = await clerk.Send(HttpMethod.Post, "/api/v1/billing/accounts/", new { payerPartyId = Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" },
            idempotent: true);
        account.StatusCode.ShouldBe(HttpStatusCode.Created, await account.Content.ReadAsStringAsync());
        var id = (await account.Json()).GetProperty("id").GetGuid();
        var schedule = await clerk.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{id}/schedules", new
        {
            sourceDomain = "GENERAL", policyRef = Guid.CreateVersion7(), productVersionRef = "TEST v1", frequency = "MONTHLY", startDate = start,
            endDate = start.AddMonths(11), instalmentAmount = 80m,
        }, idempotent: true);
        schedule.StatusCode.ShouldBe(HttpStatusCode.OK, await schedule.Content.ReadAsStringAsync());
        return (id, start);
    }

    private async Task RunAsOf(DateOnly day, HttpClient? client = null)
    {
        var run = await (client ?? _clerk).PostAsJsonAsync("/api/v1/testing/daily-jobs", new { asOf = day });
        run.StatusCode.ShouldBe(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
    }

    private async Task RetryAfterPublishing(Guid? tenant = null)
    {
        var tenantId = tenant ?? Tenant;
        var activated = new StaticDataVersionActivated(null,
            new StaticRecordRef("billing", "cfg_arrears_ageing_buckets", "ARREARS_AGEING_BUCKETS", "billing", "arrears-ageing-buckets", Guid.CreateVersion7(), "D1", 1, false),
            Today, null);
        var envelope = new EventEnvelope(Guid.CreateVersion7(), tenantId, "sdm.StaticDataVersionActivated", 1, DateTimeOffset.UtcNow, "StaticRecord", Guid.CreateVersion7(),
            Guid.CreateVersion7(), null, JsonSerializer.SerializeToElement(activated, JsonSerializerOptions.Web));
        await _factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);
        await _factory.Services.GetRequiredService<ParkingJob>().RunForTenant(tenantId, CancellationToken.None);
        await _factory.DrainOutbox();
    }

    private Task<DateOnly> InvoiceDueDate(Guid account) =>
        fixture.Database.ScalarAsOwner<DateOnly>("SELECT due_date FROM billing.invoice WHERE billing_account_id = @a", ("a", account));

    private Task<string?> InvoiceStatus(Guid account) =>
        fixture.Database.ScalarAsOwner<string>("SELECT status FROM billing.invoice WHERE billing_account_id = @a", ("a", account));

    private Task<long> OpenCases(Guid account) =>
        fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM billing.arrears_case WHERE billing_account_id = @a AND status = 'OPEN'", ("a", account));

    private Task<string?> Parked(Guid account) =>
        fixture.Database.ScalarAsOwner<string>("""
            SELECT prerequisite_code || ' ' || prerequisite_item FROM integration.parked_delivery
            WHERE consumer = @c AND status = 'PARKED' AND payload ->> 'billingAccountId' = @a
            """, ("c", Consumer), ("a", account.ToString()));

    private Task<string?> ParkedStatus(Guid account) =>
        fixture.Database.ScalarAsOwner<string>(
            "SELECT status FROM integration.parked_delivery WHERE consumer = @c AND payload ->> 'billingAccountId' = @a",
            ("c", Consumer), ("a", account.ToString()));
}
