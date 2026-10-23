/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Insys.Testing;

/// <summary>The Billing and Payments static data a money flow needs: monthly billing by debit order, collected by the simulator provider.</summary>
public static class MoneySeed
{
    public static async Task SeedBillingAndPayments(this TestDatabase database, Guid tenantId, DateOnly from)
    {
        await database.SeedStatic(tenantId, "billing.ref_billing_frequencies", "ACTIVE", "MONTHLY", from, ("label", "Monthly"), ("periods_per_year", 12));
        await database.SeedStatic(tenantId, "billing.ref_collection_methods", "ACTIVE", "DEBIT_ORDER", from, ("method_code", "DEBIT_ORDER"), ("payment_method", "DEBIT_ORDER"),
            ("lead_time", TimeSpan.FromDays(2)));
        await database.SeedStatic(tenantId, "billing.ref_adjustment_and_reversal_reason_codes", "ACTIVE", "GOODWILL", from, ("label", "Goodwill"), ("requires_approval", false),
            ("accounting_impact", "CREDIT"));
        await database.SeedStatic(tenantId, "payment.ref_payment_methods", "ACTIVE", "DEBIT_ORDER", from, ("method_code", "DEBIT_ORDER"), ("direction", "COLLECT"),
            ("currencies", new[] { "ZAR" }), ("countries", new[] { "ZA" }));
        await database.SeedStatic(tenantId, "payment.mst_payment_provider_and_bank_register", "ACTIVE", "SIMULATOR", from, ("provider_code", "SIMULATOR"),
            ("type", "PAYMENT_GATEWAY"), ("supported_methods", new[] { "DEBIT_ORDER", "EFT" }), ("credential_ref", "vault:simulator"));
        foreach (var (status, platform) in new[] { ("PAID", "SUCCEEDED"), ("BOUNCED", "FAILED") })
        {
            await database.SeedStatic(tenantId, "payment.ref_payment_status_mappings", "ACTIVE", $"SIM-{status}", from, ("provider_code", "SIMULATOR"),
                ("provider_status", status), ("platform_status", platform), ("final", true));
        }
    }

    /// <summary>An account billed monthly by debit order, a schedule starting today, and its first invoice; returns the account and invoice.</summary>
    public static async Task<(Guid Account, Guid Invoice)> IssueInvoice(this HttpClient client, decimal instalment, DateOnly today)
    {
        var account = await client.Send(HttpMethod.Post, "/api/v1/billing/accounts/", new { payerPartyId = Guid.CreateVersion7(), currency = "ZAR", collectionMethod = "DEBIT_ORDER" },
            idempotent: true);
        await Expect(account, HttpStatusCode.Created);
        var accountId = (await account.Json()).GetProperty("id").GetGuid();
        await Expect(await client.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{accountId}/schedules", new
        {
            sourceDomain = "GENERAL", policyRef = Guid.CreateVersion7(), productVersionRef = "TEST v1", frequency = "MONTHLY", startDate = today,
            endDate = today.AddMonths(11), instalmentAmount = instalment,
        }, idempotent: true), HttpStatusCode.OK);
        var issued = await client.Send(HttpMethod.Post, $"/api/v1/billing/accounts/{accountId}/invoices", new { asOf = today }, idempotent: true);
        await Expect(issued, HttpStatusCode.OK);
        return (accountId, (await issued.Json()).EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    /// <summary>A callback from the simulator provider, signed with its test secret.</summary>
    public static async Task<HttpResponseMessage> SimulatorCallback(this HttpClient client, TestDatabase database, Guid payment, string status)
    {
        var externalRef = await database.ScalarAsOwner<string>("SELECT external_ref FROM payment.payment WHERE id = @p", ("p", payment));
        var body = JsonSerializer.Serialize(new { externalRef, status }, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/providers/SIMULATOR/callbacks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestTokens.SimulatorCallbackSecret), Encoding.UTF8.GetBytes(body))));
        return await client.SendAsync(request);
    }

    private static async Task Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status)
        {
            throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }
}
