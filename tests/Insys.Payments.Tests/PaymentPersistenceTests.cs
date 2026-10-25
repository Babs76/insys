/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments.Tests
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
using Insys.Payments.Contracts;

namespace Insys.Payments.Tests;

/// <summary>
/// Payments against the real schema, through the API: a reversal writes a reversing allocation row for each allocation,
/// which the original points to (fk_payment_allocation_reversed_by_id), and the invoice reopens; independent of any
/// domain module.
/// </summary>
public sealed class PaymentPersistenceTests(PostgresServer server) : IAsyncLifetime
{
    private static readonly Guid Tenant = TestTenants.A;
    private static readonly DateOnly Today = TestClock.Today;
    private TestDatabase _db = null!;
    private InsysApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await server.CreateMigratedDatabase();
        await _db.SeedTenant(Tenant, "TENA");
        await _db.UserWithPermissions(Tenant, "cashier", BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate,
            BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate, PaymentPermissions.PaymentRead, PaymentPermissions.PaymentResolve, PaymentPermissions.PaymentUpdate,
            PaymentPermissions.CallbackEndpointUpdate);
        await _db.SeedBillingAndPayments(Tenant, Today.AddDays(-30));
        _factory = new InsysApiFactory(_db);
        _client = _factory.ClientFor(Tenant, "cashier");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Reversal_WritesReversingAllocations_AndTheInvoiceReopens()
    {
        var payment = await PaidInvoice(250m);

        var reversed = await _client.PostAsJsonAsync($"/api/v1/payments/payments/{payment.Payment}/reverse", new { reason = "Returned unpaid" });

        reversed.StatusCode.ShouldBe(HttpStatusCode.OK, await reversed.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        var view = await reversed.Json();
        view.GetProperty("status").GetString().ShouldBe("Reversed");
        (await _db.ScalarAsOwner<long>("""
            SELECT count(*) FROM payment.payment_allocation o JOIN payment.payment_allocation r ON r.tenant_id = o.tenant_id AND r.id = o.reversed_by_id
            WHERE o.payment_id = @p AND r.allocation_rule = 'REVERSAL' AND r.amount = o.amount
            """, ("p", payment.Payment))).ShouldBe(1L);
        (await (await _client.GetAsync($"/api/v1/billing/invoices/{payment.Invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Issued");
    }

    [Fact]
    public async Task ReversedPayment_IsNotReversedAgain_NorAllocatedAgain()
    {
        var payment = await PaidInvoice(250m);
        (await _client.PostAsJsonAsync($"/api/v1/payments/payments/{payment.Payment}/reverse", new { reason = "Returned unpaid" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync($"/api/v1/payments/payments/{payment.Payment}/reverse", new { reason = "Again" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _client.PostAsync($"/api/v1/payments/payments/{payment.Payment}/allocate", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM payment.payment_allocation WHERE payment_id = @p", ("p", payment.Payment))).ShouldBe(2L);
    }

    /// <summary>An invoice collected by debit order and confirmed by the provider: received and allocated.</summary>
    private async Task<(Guid Invoice, Guid Payment)> PaidInvoice(decimal amount)
    {
        var (_, invoice) = await _client.IssueInvoice(amount, Today);
        await _factory.DrainOutbox();
        var payment = await _db.ScalarAsOwner<Guid>("SELECT id FROM payment.payment WHERE business_ref_id = @i", ("i", invoice));
        (await _client.SimulatorCallback(_factory, _db, payment, "PAID")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await (await _client.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Paid");
        return (invoice, payment);
    }
}
