using System.Net;
using System.Net.Http.Json;
using Insys.Party.Contracts;
using Insys.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Payee bank accounts (party.bank_account, V0018, ADR-013): encrypted at rest, never returned or published in clear; a
/// change is a new account and the previous one is superseded; verified by a second person through Workflow
/// (BANK_ACCOUNT_VERIFICATION, SRS-002, WFL-004); tenant-isolated.
/// </summary>
public sealed class BankAccountTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private const string Number = "62 0001 2345 9021";
    private InsysApiFactory _factory = null!;
    private static readonly string[] Approving = [WorkflowPermissions.ApprovalRead, WorkflowPermissions.ApprovalApprove];
    private HttpClient _clerk = null!;
    private HttpClient _verifier = null!;
    private HttpClient _approverWithoutVerify = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new InsysApiFactory(fixture.Database);
        var db = fixture.Database;
        var clerk = await db.UserWithPermissions(DatabaseFixture.TenantA, "bank-clerk", [PartyPermissions.PartyCreate, PartyPermissions.PartyRead,
            PartyPermissions.BankAccountCreate, PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify, .. Approving]);
        var verifier = await db.UserWithPermissions(DatabaseFixture.TenantA, "bank-verifier", [PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify, .. Approving]);
        var approver = await db.UserWithPermissions(DatabaseFixture.TenantA, "bank-approver-no-verify", Approving);
        await db.UserWithPermissions(DatabaseFixture.TenantB, "bank-clerk-b", PartyPermissions.BankAccountRead, PartyPermissions.BankAccountVerify);

        // Every user here holds BANK_ACCOUNT_VERIFICATION authority, the clerk included, so only the rules under test refuse.
        var role = await db.RoleWithPermissions(DatabaseFixture.TenantA);
        foreach (var user in new[] { clerk, verifier, approver })
        {
            await db.AssignRole(DatabaseFixture.TenantA, user, role);
        }

        await db.AllowBankAccountVerification(DatabaseFixture.TenantA, role, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        _clerk = _factory.ClientFor(DatabaseFixture.TenantA, "bank-clerk");
        _verifier = _factory.ClientFor(DatabaseFixture.TenantA, "bank-verifier");
        _approverWithoutVerify = _factory.ClientFor(DatabaseFixture.TenantA, "bank-approver-no-verify");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AccountNumber_IsEncryptedAtRest_AndNeverReturnedOrPublished()
    {
        var party = await _clerk.RegisterPerson("Encrypted");
        var account = await _clerk.AddVerifiedBankAccount(_verifier, _factory, party, Number);

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
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM party.outbox_event WHERE payload::text LIKE '%6200012345%'")).ShouldBe(0L);
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM workflow.approval WHERE business_ref_id = @a AND subject_hash IS NOT NULL", ("a", account)))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task Change_IsANewAccount_ThePreviousIsSuperseded_AndAudited()
    {
        var party = await _clerk.RegisterPerson("Changed");
        var first = await _clerk.AddVerifiedBankAccount(_verifier, _factory, party, "62000111222");

        var second = await _clerk.AddVerifiedBankAccount(_verifier, _factory, party, "62000333444");

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

        // There is no verify endpoint beside Workflow.
        (await _clerk.PostAsJsonAsync($"/api/v1/party/parties/{party}/bank-accounts/{account}/verify", new { verificationRef = "X" })).StatusCode
            .ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task TheMaker_CannotApproveTheirOwnAccount_NorCanACheckerWithoutTheVerifyPermission_SRS002()
    {
        var party = await _clerk.RegisterPerson("OwnApproval");
        var account = await Add(party, "62000123123");
        var approval = await Approval(account);

        (await (await _clerk.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("MAKER_CHECKER_SAME_PERSON");
        (await (await _approverWithoutVerify.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{approval}/approve", ifMatch: "\"1\"")).Code()).ShouldBe("FORBIDDEN");
        await _factory.DrainOutbox();
        (await Status(party, account)).ShouldBe("PENDING_VERIFICATION");

        await _verifier.DecideBankAccount(_factory, account, approve: true);
        (await Status(party, account)).ShouldBe("ACTIVE");
        await _factory.DrainOutbox();
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM audit.audit_event WHERE operation = 'VerifyBankAccount' AND target_id = @a", ("a", account.ToString())))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task AnUnverifiedOrRejectedAccount_CannotBePaid()
    {
        var party = await _clerk.RegisterPerson("Rejected");
        var account = await Add(party, "62000456456");
        var accounts = _factory.Services.GetRequiredService<IBankAccounts>();
        var details = _factory.Services.GetRequiredService<IBankAccountDetails>();
        (await accounts.Get(DatabaseFixture.TenantA, account, CancellationToken.None))!.IsPayable.ShouldBeFalse();
        (await details.ForPayment(DatabaseFixture.TenantA, account, CancellationToken.None)).ShouldBeNull();

        await _verifier.DecideBankAccount(_factory, account, approve: false);

        (await Status(party, account)).ShouldBe("REJECTED");
        (await accounts.Get(DatabaseFixture.TenantA, account, CancellationToken.None))!.IsPayable.ShouldBeFalse();
        (await accounts.Current(DatabaseFixture.TenantA, party, "ZAR", CancellationToken.None)).ShouldBeNull();
        (await details.ForPayment(DatabaseFixture.TenantA, account, CancellationToken.None)).ShouldBeNull();

        // A rejected account is never activated: a new account can be added and verified in its place.
        var replacement = await _clerk.AddVerifiedBankAccount(_verifier, _factory, party, "62000456457");
        (await accounts.Current(DatabaseFixture.TenantA, party, "ZAR", CancellationToken.None))!.Id.ShouldBe(replacement);
        (await Status(party, account)).ShouldBe("REJECTED");
    }

    [Fact]
    public async Task AChangeWhileApprovalIsPending_WithdrawsThatApproval_AndNeedsANewOne()
    {
        var party = await _clerk.RegisterPerson("PendingChange");
        var first = await Add(party, "62000789789");
        var firstApproval = await Approval(first);
        var second = await Add(party, "62000789790");

        (await fixture.Database.ScalarAsOwner<string>("SELECT status FROM workflow.approval WHERE id = @a", ("a", firstApproval))).ShouldBe("WITHDRAWN");
        (await (await _verifier.Send(HttpMethod.Post, $"/api/v1/workflow/approvals/{firstApproval}/approve", ifMatch: "\"2\"")).Code())
            .ShouldBe("INVALID_STATE_TRANSITION");
        (await Status(party, first)).ShouldBe("SUPERSEDED");
        (await Status(party, second)).ShouldBe("PENDING_VERIFICATION");

        await _verifier.DecideBankAccount(_factory, second, approve: true);
        (await Status(party, second)).ShouldBe("ACTIVE");
        (await Status(party, first)).ShouldBe("SUPERSEDED");
    }

    private async Task<Guid> Add(Guid party, string number)
    {
        var added = await _clerk.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/bank-accounts", new
        {
            accountNumber = number, bankCode = "632005", accountHolderName = "Holder", accountType = "CURRENT", currency = "ZAR",
        }, idempotent: true);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        await _factory.DrainOutbox();
        return (await added.Json()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Approval(Guid account)
    {
        var page = await (await _verifier.GetAsync($"/api/v1/workflow/approvals?status=Requested&businessRefType=BANK_ACCOUNT&businessRefId={account}")).Json();
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<string> Status(Guid party, Guid account) =>
        (await (await _clerk.GetAsync($"/api/v1/party/parties/{party}/bank-accounts")).Json()).EnumerateArray()
        .Single(a => a.GetProperty("id").GetGuid() == account).GetProperty("status").GetString()!;

    [Fact]
    public async Task VerifiedAccount_IsDecryptedOnlyForPayment()
    {
        var party = await _clerk.RegisterPerson("Payable");
        var account = await _clerk.AddVerifiedBankAccount(_verifier, _factory, party, Number);

        var details = await _factory.Services.GetRequiredService<IBankAccountDetails>().ForPayment(DatabaseFixture.TenantA, account, CancellationToken.None);

        details!.AccountNumber.ShouldBe("6200012345902" + "1");
        details.ToString().ShouldNotContain("2345");
    }
}
