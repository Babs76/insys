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

    /// <summary>Adds a bank account and verifies it; the user needs bank_account.create and bank_account.verify. Returns the account's id.</summary>
    public static async Task<Guid> AddVerifiedBankAccount(this HttpClient client, Guid partyId, string accountNumber, string currency = "ZAR")
    {
        var added = await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{partyId}/bank-accounts", new
        {
            accountNumber, bankCode = "632005", accountHolderName = "Test account holder", accountType = "CURRENT", currency,
        }, idempotent: true);
        var accountId = (await Expect(added, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await Expect(await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{partyId}/bank-accounts/{accountId}/verify", new { verificationRef = "AVS-OK-" + accountNumber[^4..] }),
            HttpStatusCode.OK);
        return accountId;
    }

    private static async Task<System.Text.Json.JsonElement> Expect(HttpResponseMessage response, HttpStatusCode status) =>
        response.StatusCode == status
            ? await response.Json()
            : throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
}
