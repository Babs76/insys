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

namespace Insys.Testing;

/// <summary>Parties with verified bank accounts, through the API, for tests that pay someone.</summary>
public static class PartySeed
{
    /// <summary>Registers an individual; the client's user needs party.create.</summary>
    public static async Task<Guid> RegisterPerson(this HttpClient client, string surname)
    {
        var response = await client.Send(HttpMethod.Post, "/api/v1/party/parties", new
        {
            individual = new { firstNames = "Test", surname, birthDate = "1985-06-15", nationality = "ZA" },
        }, idempotent: true);
        return (await Expect(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Adds a bank account and has it verified through Workflow (ADR-013): the maker adds it, and the checker (another
    /// user with bank_account.verify, approval.approve and BANK_ACCOUNT_VERIFICATION authority) approves it. Returns the account's id.
    /// </summary>
    public static async Task<Guid> AddVerifiedBankAccount(this HttpClient maker, HttpClient checker, InsysApiFactory factory, Guid partyId, string accountNumber,
        string currency = "ZAR")
    {
        var added = await maker.Send(HttpMethod.Post, $"/api/v1/party/parties/{partyId}/bank-accounts", new
        {
            accountNumber, bankCode = "632005", accountHolderName = "Test account holder", accountType = "CURRENT", currency,
        }, idempotent: true);
        var accountId = (await Expect(added, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await factory.DrainOutbox();
        await checker.DecideBankAccount(factory, accountId, approve: true);
        return accountId;
    }

    /// <summary>The checker approves (or rejects) the account's waiting verification approval in Workflow.</summary>
    public static async Task DecideBankAccount(this HttpClient checker, InsysApiFactory factory, Guid accountId, bool approve)
    {
        var page = await Expect(await checker.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType=BANK_ACCOUNT&businessRefId={accountId}"),
            HttpStatusCode.OK);
        var approval = page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var decided = approve
            ? await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")
            : await checker.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/reject", new { reason = "Account holder does not match" }, ifMatch: "\"1\"");
        if (decided.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Bank account verification was not decided: {decided.StatusCode} {await decided.Content.ReadAsStringAsync()}");
        }

        await factory.DrainOutbox();
    }

    /// <summary>Gives a role BANK_ACCOUNT_VERIFICATION authority in Workflow (workflow.cfg_delegation_and_approval_authority).</summary>
    public static Task AllowBankAccountVerification(this TestDatabase database, Guid tenantId, string roleCode, DateOnly from) =>
        database.SeedStatic(tenantId, "workflow.cfg_delegation_and_approval_authority", "EFFECTIVE", $"AUTH-BAV-{roleCode}", from, ("role_code", roleCode),
            ("transaction_type", "BANK_ACCOUNT_VERIFICATION"), ("limit_value", 0m), ("delegation_allowed", false));

    private static async Task<System.Text.Json.JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status) =>
        response.StatusCode == status
            ? await response.Json()
            : throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
}
