using System.Net;
using System.Net.Http.Json;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Payee bank accounts (party.bank_account, V0018): encrypted at rest, never returned or published in clear; a change
/// is a new account and the previous one is superseded; tenant-isolated.
/// </summary>
public sealed class BankAccountTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Number = "62 0001 2345 9021";
    private InsysApiFactory _factory = null!;
    private HttpClient _clerk = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "bank-clerk", PartyPermissions.PartyCreate, PartyPermissions.PartyRead,
            PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify);
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantB, "bank-clerk-b", PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify);
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "bank-clerk");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AccountNumber_IsEncryptedAtRest_AndNeverReturnedOrPublished()
    {
        var party = await _clerk.RegisterPerson("Encrypted");
        var account = await _clerk.AddVerifiedBankAccount(party, Number);

        var listed = await (await _clerk.GetAsync($"/api/v1/party/parties/{party}/bank-accounts")).Content.ReadAsStringAsync();
        listed.ShouldNotContain("12345");
        listed.ShouldContain("\"last4\":\"9021\"");
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM party.bank_account WHERE id = @a AND position(convert_to('6200012345', 'UTF8') in account_number_enc) = 0 AND length(account_number_hash) = 64",
            ("a", account))).ShouldBe(1L);
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM party.outbox_event WHERE event_type = 'party.PartyBankAccountChanged' AND payload::text LIKE '%2345%'")).ShouldBe(0L);
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM party.outbox_event WHERE event_type = 'party.PartyBankAccountChanged' AND payload ->> 'bankAccountId' = @a", ("a", account.ToString())))
            .ShouldBe(2L);
    }

    [Fact]
    public async Task Change_IsANewAccount_ThePreviousIsSuperseded_AndAudited()
    {
        var party = await _clerk.RegisterPerson("Changed");
        var first = await _clerk.AddVerifiedBankAccount(party, "62000111222");

        var second = await _clerk.AddVerifiedBankAccount(party, "62000333444");

        var accounts = (await (await _clerk.GetAsync($"/api/v1/party/parties/{party}/bank-accounts")).Json()).EnumerateArray().ToList();
        accounts.Single(a => a.GetProperty("id").GetGuid() == first).GetProperty("status").GetString().ShouldBe("SUPERSEDED");
        accounts.Single(a => a.GetProperty("id").GetGuid() == first).GetProperty("supersededById").GetGuid().ShouldBe(second);
        accounts.Single(a => a.GetProperty("id").GetGuid() == second).GetProperty("status").GetString().ShouldBe("ACTIVE");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>(
            "SELECT count(*) FROM audit.audit_event WHERE operation = 'AddBankAccount' AND target_id IN (@a, @b)", ("a", first.ToString()), ("b", second.ToString())))
            .ShouldBe(2L);
    }

    [Fact]
    public async Task UnverifiedAccount_IsNotPayable_AndOtherTenantsCannotSeeIt()
    {
        var party = await _clerk.RegisterPerson("Pending");
        var added = await (await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = "62000999888", bankCode = "632005", accountHolderName = "Pending", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true)).Json();
        added.GetProperty("status").GetString().ShouldBe("PENDING_VERIFICATION");
        var account = added.GetProperty("id").GetGuid();

        var accounts = _factory.Services.GetRequiredService<IBankAccounts>();
        (await accounts.Get(DatabaseFixture.TenantA, account, CancellationToken.None))!.IsPayable.ShouldBeFalse();
        (await accounts.Get(DatabaseFixture.TenantB, account, CancellationToken.None)).ShouldBeNull();
        (await _factory.Services.GetRequiredService<IBankAccountDetails>().ForPayment(DatabaseFixture.TenantA, account, CancellationToken.None)).ShouldBeNull();

        var other = _factory.ClientFor(DatabaseFixture.TenantB, "bank-clerk-b");
        (await other.GetAsync($"/api/v1/party/parties/{party}/bank-accounts")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/v1/party/parties/{party}/bank-accounts/{account}/verify", new { verificationRef = "X" })).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VerifiedAccount_IsDecryptedOnlyForPayment()
    {
        var party = await _clerk.RegisterPerson("Payable");
        var account = await _clerk.AddVerifiedBankAccount(party, Number);

        var details = await _factory.Services.GetRequiredService<IBankAccountDetails>().ForPayment(DatabaseFixture.TenantA, account, CancellationToken.None);

        details!.AccountNumber.ShouldBe("6200012345902" + "1");
        details.ToString().ShouldNotContain("2345");
    }
}
