/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing.Tests
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
using Insys.Billing.Domain.Billing;

namespace Insys.Billing.Tests;

/// <summary>
/// Billing against the real schema, through the API: a credit note and its adjustment are stored together, in the
/// order the foreign keys need (fk_adjustment_credit_note_id), independent of any domain module (BIL-004).
/// </summary>
public sealed class BillingPersistenceTests(PostgresServer server) : IAsyncLifetime
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
        await _db.UserWithPermissions(Tenant, "billing-clerk", BillingPermissions.AccountRead, BillingPermissions.AccountCreate, BillingPermissions.AccountUpdate,
            BillingPermissions.InvoiceRead, BillingPermissions.InvoiceCreate);
        await _db.SeedBillingAndPayments(Tenant, Today.AddDays(-30));
        _factory = new InsysApiFactory(_db);
        _client = _factory.ClientFor(Tenant, "billing-clerk");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task CreditNote_IsStoredWithItsAdjustment_AndTheInvoiceIsNotOverwritten_BIL004()
    {
        var (_, invoice) = await _client.IssueInvoice(300m, Today);

        var credit = await _client.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice}/credit-notes", new { amount = 120m, reasonCode = "GOODWILL" }, idempotent: true);

        credit.StatusCode.ShouldBe(HttpStatusCode.Created, await credit.Content.ReadAsStringAsync());
        var creditId = (await credit.Json()).GetProperty("id").GetGuid();
        (await _db.ScalarAsOwner<Guid>("SELECT credit_note_id FROM billing.adjustment WHERE invoice_id = @i", ("i", invoice))).ShouldBe(creditId);
        (await _db.ScalarAsOwner<string>("SELECT document_type FROM billing.invoice WHERE id = @c", ("c", creditId))).ShouldBe("CREDIT_NOTE");
        (await _db.ScalarAsOwner<decimal>("SELECT total_amount FROM billing.invoice WHERE id = @i", ("i", invoice))).ShouldBe(300m);
    }

    [Fact]
    public async Task SecondCreditNote_UpToTheRemainder_CreditsTheInvoiceFully()
    {
        var (_, invoice) = await _client.IssueInvoice(300m, Today);
        (await _client.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice}/credit-notes", new { amount = 100m, reasonCode = "GOODWILL" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await (await _client.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice}/credit-notes", new { amount = 200.01m, reasonCode = "GOODWILL" }, idempotent: true))
            .Code()).ShouldBe(BillingErrorCodes.CreditExceedsInvoice);
        (await _client.Send(HttpMethod.Post, $"/api/v1/billing/invoices/{invoice}/credit-notes", new { amount = 200m, reasonCode = "GOODWILL" }, idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await (await _client.GetAsync($"/api/v1/billing/invoices/{invoice}")).Json()).GetProperty("status").GetString().ShouldBe("Credited");
        (await _db.ScalarAsOwner<long>("SELECT count(*) FROM billing.adjustment WHERE invoice_id = @i", ("i", invoice))).ShouldBe(2L);
    }
}
