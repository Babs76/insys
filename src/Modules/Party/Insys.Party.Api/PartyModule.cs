using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Party.Api.StaticData;
using Insys.Party.Application;
using Insys.Party.Contracts;
using Insys.Party.Domain.Parties;
using Insys.Party.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Party.Api;

public sealed record RegisterPartyRequest(IndividualData? Individual, OrganisationData? Organisation);

public sealed record ContactPointRequest(string ChannelType, string Value, bool IsPrimary);

/// <param name="Number">The document number. It is stored encrypted and never returned.</param>
public sealed record IdentifierRequest(string DocType, string Country, string Number);

public sealed record BankAccountRequest(string AccountNumber, string BankCode, string? BranchCode, string AccountHolderName, string AccountType, string Currency);

public sealed record VerifyIdentifierRequest(string VerificationRef);

public sealed record RelationshipRequest(Guid ToPartyId, string RelationshipCode, DateOnly ValidFrom);

public sealed record PartyRoleRequest(string RoleCode, string Domain, string ContextType, Guid ContextId, DateOnly ValidFrom);

public sealed record DeathRequest(DateOnly DeceasedDate);

public sealed record MergePartiesRequest(Guid SurvivorId, Guid MergedId, string Reason);

public sealed record ConsentRequest(string PurposeCode, string CaptureChannel, string? EvidenceRef);

/// <summary>The Party module: people and organisations, their details, identifiers, relationships, roles and consent (SRS PTY-001 to PTY-005).</summary>
public sealed class PartyModule : IModule
{
    public string Name => "Party";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPartyInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        PartyStaticDataEndpoints.MapStaticData(api);
        var parties = api.MapGroup("/party/parties").WithTags(Name);

        parties.MapPost("/", async (RegisterPartyRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new RegisterParty(r.Individual, r.Organisation), ct)).ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/party/parties/{id}", new { id });
                }))
            .WithName("PartyRegisterParty")
            .WithSummary("Register an individual or an organisation")
            .WithDescription("Give exactly one of individual and organisation; the kind never changes (PTY-001).")
            .RequirePermission(PartyPermissions.PartyCreate)
            .RequireIdempotencyKey();

        parties.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetParty(id), ct)).ToHttp(party =>
                {
                    Concurrency.SetETag(http, party.RowVersion);
                    return Results.Ok(party);
                }))
            .WithName("PartyGetParty")
            .WithSummary("Get a party with its addresses, contact points, identifiers (without numbers), roles and relationships")
            .RequirePermission(PartyPermissions.PartyRead);

        parties.MapPut("/{id:guid}/individual", (Guid id, IndividualData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new UpdateIndividualDetails(id, r, v), ct)))
            .WithName("PartyUpdateIndividual")
            .WithSummary("Update an individual's details")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPut("/{id:guid}/organisation", (Guid id, OrganisationData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new UpdateOrganisationDetails(id, r, v), ct)))
            .WithName("PartyUpdateOrganisation")
            .WithSummary("Update an organisation's details")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/addresses", (Guid id, AddressData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AddAddress(id, r, v), ct)))
            .WithName("PartyAddAddress")
            .WithSummary("Add an address; it ends the current address of the same type")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/contact-points", (Guid id, ContactPointRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AddContactPoint(id, r.ChannelType, r.Value, r.IsPrimary, v), ct)))
            .WithName("PartyAddContactPoint")
            .WithSummary("Add a contact point")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/bank-accounts", async (Guid id, BankAccountRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AddBankAccount(id, r.AccountNumber, r.BankCode, r.BranchCode, r.AccountHolderName, r.AccountType, r.Currency), ct))
                .ToHttp(v => Results.Created($"/api/v1/party/parties/{id}/bank-accounts", v)))
            .WithName("PartyAddBankAccount")
            .WithSummary("Add a bank account, pending a second person's verification in Workflow; the party's current account in the currency is superseded")
            .WithDescription("The account number is encrypted at once and never returned: responses show its last four digits.")
            .RequirePermission(PartyPermissions.BankAccountCreate)
            .RequireIdempotencyKey();

        parties.MapGet("/{id:guid}/bank-accounts", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListBankAccounts(id), ct)).ToHttp(Results.Ok))
            .WithName("PartyListBankAccounts")
            .WithSummary("List a party's bank accounts, current and superseded, without their numbers")
            .RequirePermission(PartyPermissions.BankAccountRead);

        parties.MapPost("/{id:guid}/identifiers", async (Guid id, IdentifierRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                return expected.IsFailure
                    ? Problems.For(expected.Error)
                    : (await dispatcher.Send(new AddIdentifier(id, r.DocType, r.Country, r.Number, expected.Value), ct)).ToHttp(identifierId =>
                    {
                        Concurrency.SetETag(http, expected.Value + 1);
                        return Results.Created($"/api/v1/party/parties/{id}", new { id = identifierId });
                    });
            })
            .WithName("PartyAddIdentifier")
            .WithSummary("Add an identity document number, unique per type and country in the tenant")
            .WithDescription("The number is checked against the document type's format, stored encrypted with a keyed hash, and never returned.")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/identifiers/{identifierId:guid}/verify", (Guid id, Guid identifierId, VerifyIdentifierRequest r, IDispatcher dispatcher,
                HttpContext http, CancellationToken ct) => Versioned(http, v => dispatcher.Send(new VerifyIdentifier(id, identifierId, r.VerificationRef, v), ct)))
            .WithName("PartyVerifyIdentifier")
            .WithSummary("Record that an identifier was verified")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/relationships", (Guid id, RelationshipRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new RecordRelationship(id, r.ToPartyId, r.RelationshipCode, r.ValidFrom, v), ct)))
            .WithName("PartyRecordRelationship")
            .WithSummary("Record a relationship from this party to another")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/roles", (Guid id, PartyRoleRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AssignPartyRole(id, r.RoleCode, r.Domain, r.ContextType, r.ContextId, r.ValidFrom, v), ct)))
            .WithName("PartyAssignRole")
            .WithSummary("Assign a role in a context; a party holds many roles (PTY-002)")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/death", (Guid id, DeathRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new RecordDeath(id, r.DeceasedDate, v), ct)))
            .WithName("PartyRecordDeath")
            .WithSummary("Record an individual's death")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/anonymise", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AnonymiseParty(id, v), ct)))
            .WithName("PartyAnonymise")
            .WithSummary("Anonymise a deceased or dormant party whose retention has ended")
            .RequirePermission(PartyPermissions.PartyAnonymise);

        parties.MapGet("/{id:guid}/consents", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetConsents(id), ct)).ToHttp(Results.Ok))
            .WithName("PartyGetConsents")
            .WithSummary("The party's current consent per purpose")
            .RequirePermission(PartyPermissions.ConsentRead);

        parties.MapPost("/{id:guid}/consents/grant", async (Guid id, ConsentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new GrantConsent(id, r.PurposeCode, r.CaptureChannel, r.EvidenceRef), ct)).ToHttp(Results.Ok))
            .WithName("PartyGrantConsent")
            .WithSummary("Record a consent; the ledger is append-only (PTY-005)")
            .RequirePermission(PartyPermissions.ConsentUpdate);

        parties.MapPost("/{id:guid}/consents/withdraw", async (Guid id, ConsentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new WithdrawConsent(id, r.PurposeCode, r.CaptureChannel, r.EvidenceRef), ct)).ToHttp(Results.Ok))
            .WithName("PartyWithdrawConsent")
            .WithSummary("Record a withdrawal of consent")
            .RequirePermission(PartyPermissions.ConsentUpdate);

        api.MapGroup("/party/merges").WithTags(Name).MapPost("/", async (MergePartiesRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new MergeParties(r.SurvivorId, r.MergedId, r.Reason), ct)).ToHttp(id => Results.Ok(new { mergedId = id, survivorId = r.SurvivorId })))
            .WithName("PartyMergeParties")
            .WithSummary("Merge a duplicate party into its survivor")
            .WithDescription("The merged party becomes read-only and points to the survivor; the merge is kept in an auditable history (PTY-004).")
            .RequirePermission(PartyPermissions.PartyMerge)
            .RequireIdempotencyKey();
    }

    private static async Task<IResult> Versioned(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }
}
