/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using System.ComponentModel;
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

/// <param name="ConfirmNew">After a PARTY_POSSIBLE_DUPLICATE: the person is not any of the candidates; Reason says why (OQ-127).</param>
public sealed record RegisterPartyRequest(IndividualData? Individual, OrganisationData? Organisation, bool ConfirmNew = false, string? Reason = null);

public sealed record ContactPointRequest([property: BuildingBlocks.Domain.CodeList(Tables.ContactChannels)] string ChannelType, string Value, bool IsPrimary);

/// <param name="Number">The document number. It is stored encrypted and never returned.</param>
public sealed record IdentifierRequest([property: BuildingBlocks.Domain.CodeList(Tables.IdentityDocuments)] string DocType,
    [property: BuildingBlocks.Domain.CodeList(Tables.Countries)] string Country, string Number);

public sealed record PreferredLanguageRequest([property: BuildingBlocks.Domain.CodeList(Tables.Languages)] string? Language);

public sealed record BankAccountRequest(string AccountNumber, string BankCode, string? BranchCode, string AccountHolderName, string AccountType, string Currency);

public sealed record VerifyIdentifierRequest(string VerificationRef);

public sealed record RelationshipRequest(Guid ToPartyId, [property: BuildingBlocks.Domain.CodeList(Tables.RelationshipTypes)] string RelationshipCode, DateOnly ValidFrom);

public sealed record PartyRoleRequest([property: BuildingBlocks.Domain.CodeList(Tables.RoleTypes)] string RoleCode, string Domain, string ContextType, Guid ContextId, DateOnly ValidFrom);

/// <param name="Resolution">LINK or NEW.</param>
public sealed record MatchResolutionRequest([property: BuildingBlocks.Domain.OneOf("LINK", "NEW")] string Resolution, Guid? ExistingPartyId);

public sealed record DeathRequest(DateOnly DeceasedDate);

public sealed record MergePartiesRequest(Guid SurvivorId, Guid MergedId, string Reason);

/// <summary>Party search filters (GAP-022): any combination, at least one. The body carries them, so an identifier number never appears in a URL.</summary>
/// <param name="PartyNumber">An exact party number (BR-029), P and 9 digits; its check digit is checked.</param>
public sealed record PartySearchRequest(string? Name, IdentifierFilter? Identifier, [property: BuildingBlocks.Domain.CodeList(Tables.RoleTypes)] string? RoleCode, PartyStatus? Status, string? Cursor, int? Limit,
    [property: System.ComponentModel.DataAnnotations.RegularExpression(PartyNumbers.Pattern)] string? PartyNumber = null);

public sealed record ConsentRequest([property: BuildingBlocks.Domain.CodeList(Tables.ConsentPurposes)] string PurposeCode, string CaptureChannel, string? EvidenceRef);

/// <summary>The Party module: people and organisations, their details, identifiers, relationships, roles and consent (SRS PTY-001 to PTY-005).</summary>
/// <summary>The outcome of a merge: the merged party and the survivor it is now read through.</summary>
public sealed record MergedParties(Guid MergedId, Guid SurvivorId);

public sealed class PartyModule : IModule
{
    public string Name => "Party";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => PartyFunctions.All;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddPartyInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        AuditTargetTypes.Register(nameof(BankAccount));
        AuditTargetTypes.Register(nameof(PartySearchLog));
        MapSearchLogHolds(api.MapGroup("/party/search-log/holds").WithTags(Name));
        PartyStaticDataEndpoints.MapStaticData(api);
        var parties = api.MapGroup("/party/parties").WithTags(Name);

        parties.MapPost("/search", async (PartySearchRequest r, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(r.Cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(r.Limit);
                return (await dispatcher.Send(new SearchParties(r.Name, r.Identifier, r.RoleCode, r.Status, afterId, pageSize, r.PartyNumber), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<PartySummary>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<PartySummary>>(StatusCodes.Status200OK)
            .WithName("PartySearchParties")
            .WithSummary("Search parties")
            .WithDescription("By name (at least 3 characters, anywhere in the display name), an exact identifier (document type, country and number), "
                + "a role in effect today, and status; at least one, all that are given must match. Results are summaries ordered by ID with cursor "
                + "paging; open a party for its details. Every search is written to the party access log (PTY-003, PTY-005): who, when, the filters "
                + "and the number of results, with an identifier by its keyed hash only. POST, so the identifier number is never in a URL.")
            .RequirePermission(PartyPermissions.PartyRead);

        api.MapGet("/party/search-log", async (string? searchedBy,
                [Description("An instant (ISO 8601 with its offset, for example 2026-10-28T00:00:00+02:00): searches made at or after it")] DateTimeOffset? from,
                [Description("An instant (ISO 8601 with its offset): searches made before it")] DateTimeOffset? to, string? cursor, int? limit, IDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var beforeId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var pageSize = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListPartySearches(searchedBy, from, to, beforeId, pageSize), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<PartySearchLogEntry>(items, items.Count == pageSize ? Cursor.Encode(items[^1].Id) : null)));
            })
            .WithTags(Name)
            .Produces<CursorPage<PartySearchLogEntry>>(StatusCodes.Status200OK)
            .WithName("PartyListSearches")
            .WithSummary("The party search access log, newest first")
            .WithDescription("Who searched for parties, when, with which filters and how many results (GAP-022, PTY-005). For audit roles only: "
                + "audit_event.read, not party.read. An identifier filter is shown as document type, country and keyed hash. Retention: OQ-073.")
            .RequirePermission(Audit.Contracts.AuditPermissions.Read);

        parties.MapPost("/", async (RegisterPartyRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new RegisterParty(r.Individual, r.Organisation, r.ConfirmNew, r.Reason), ct)).ToHttp(id =>
                {
                    Concurrency.SetETag(http, 1);
                    return Results.Created($"/api/v1/party/parties/{id}", new { id });
                }))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("PartyRegisterParty")
            .WithSummary("Register an individual or an organisation")
            .WithDescription("Give exactly one of individual and organisation; the kind never changes (PTY-001). A person matching an existing party (the same "
                + "names, ignoring case, and birth date) is refused with 409 PARTY_POSSIBLE_DUPLICATE and its candidates; use one of them, or send the "
                + "registration again with confirmNew true and a reason, which is recorded as reviewed and audited (OQ-127).")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(PartyPermissions.PartyCreate)
            .RequireIdempotencyKey();

        parties.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetParty(id), ct)).ToHttp(party =>
                {
                    Concurrency.SetETag(http, party.RowVersion);
                    return Results.Ok(party);
                }))
            .Produces<PartyView>(StatusCodes.Status200OK)
            .WithName("PartyGetParty")
            .WithSummary("Get a party with its addresses, contact points, identifiers (without numbers), roles and relationships")
            .RequirePermission(PartyPermissions.PartyRead);

        parties.MapPut("/{id:guid}/individual", (Guid id, IndividualData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new UpdateIndividualDetails(id, r, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyUpdateIndividual")
            .WithSummary("Update an individual's details")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPut("/{id:guid}/preferred-language", (Guid id, PreferredLanguageRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new ChangePreferredLanguage(id, r.Language, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyChangePreferredLanguage")
            .WithSummary("Set or clear an individual's preferred language, used for documents and notifications")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPut("/{id:guid}/organisation", (Guid id, OrganisationData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new UpdateOrganisationDetails(id, r, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyUpdateOrganisation")
            .WithSummary("Update an organisation's details")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/addresses", (Guid id, AddressData r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AddAddress(id, r, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyAddAddress")
            .WithSummary("Add an address; it ends the current address of the same type")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/contact-points", (Guid id, ContactPointRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AddContactPoint(id, r.ChannelType, r.Value, r.IsPrimary, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyAddContactPoint")
            .WithSummary("Add a contact point")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/bank-accounts", async (Guid id, BankAccountRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AddBankAccount(id, r.AccountNumber, r.BankCode, r.BranchCode, r.AccountHolderName, r.AccountType, r.Currency), ct))
                .ToHttp(v => Results.Created($"/api/v1/party/parties/{id}/bank-accounts", v)))
            .Produces<BankAccountView>(StatusCodes.Status201Created)
            .WithName("PartyAddBankAccount")
            .WithSummary("Add a bank account, pending a second person's verification in Workflow; the party's current account in the currency is superseded")
            .WithDescription("The account number is encrypted at once and never returned: responses show its last four digits.")
            .RequirePermission(PartyPermissions.BankAccountCreate)
            .RequireIdempotencyKey();

        parties.MapGet("/{id:guid}/bank-accounts", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListBankAccounts(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<BankAccountView>>(StatusCodes.Status200OK)
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
            .RequireIfMatch()
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("PartyAddIdentifier")
            .WithSummary("Add an identity document number, unique per type and country in the tenant")
            .WithDescription("The number is checked against the document type's format, stored encrypted with a keyed hash, and never returned.")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/identifiers/{identifierId:guid}/verify", (Guid id, Guid identifierId, VerifyIdentifierRequest r, IDispatcher dispatcher,
                HttpContext http, CancellationToken ct) => Versioned(http, v => dispatcher.Send(new VerifyIdentifier(id, identifierId, r.VerificationRef, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyVerifyIdentifier")
            .WithSummary("Record that an identifier was verified")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/relationships", (Guid id, RelationshipRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new RecordRelationship(id, r.ToPartyId, r.RelationshipCode, r.ValidFrom, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyRecordRelationship")
            .WithSummary("Record a relationship from this party to another")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/roles", (Guid id, PartyRoleRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AssignPartyRole(id, r.RoleCode, r.Domain, r.ContextType, r.ContextId, r.ValidFrom, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyAssignRole")
            .WithSummary("Assign a role in a context; a party holds many roles (PTY-002)")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/death", (Guid id, DeathRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new RecordDeath(id, r.DeceasedDate, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyRecordDeath")
            .WithSummary("Record an individual's death")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/match-resolution", (Guid id, MatchResolutionRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new ResolvePartyMatch(id, r.Resolution, r.ExistingPartyId, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("PartyResolveMatch")
            .WithSummary("Resolve a portal registration that matched an existing party: LINK it to that party, or confirm it as NEW")
            .WithDescription("External-user access §6. The party must be waiting for review (reviewStatus PENDING_REVIEW). existingPartyId is needed only for a "
                + "registration made before its match was recorded, and must be the matched party otherwise.")
            .WithEventualEffects("the PARTY_MATCH_RESOLUTION approval appears in Workflow. Approved, a LINK merges the registration into the existing party "
                + "(party.PartiesMerged) and NEW confirms it, and the review task is closed; rejected, the registration is back to PENDING_REVIEW.")
            .RequirePermission(PartyPermissions.PartyUpdate);

        parties.MapPost("/{id:guid}/anonymise", (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, v => dispatcher.Send(new AnonymiseParty(id, v), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PartyAnonymise")
            .WithSummary("Anonymise a deceased or dormant party whose retention has ended")
            .WithDescription("D-34, OQ-120: retention is checked, not assumed. Refused with 409 PARTY_ANONYMISATION_BLOCKED while anything still keeps "
                + "the party's data; the problem's `blocks` lists each (see ProblemDetails). Every Life, General and Health policy, membership and claim "
                + "linked to the party, and every payment, counts, the party's own and those of parties merged into it. A record is OPEN until it reaches a "
                + "final status; it is then IN_RETENTION for the tenant's record retention policy for its kind (POLICY, CLAIM or PAYMENT, "
                + "/party/static/record-retention-policies; working default 7 years) from the business date it reached that status, by its status "
                + "history. With no period in force for its kind it blocks as NO_RETENTION_POLICY. The audit evidence about the party blocks for the "
                + "longest audit retention class from its latest event, and a legal hold in force on any of these records or their documents blocks as "
                + "LEGAL_HOLD. A party that is not deceased or dormant is refused with 409 INVALID_STATE_TRANSITION. Erasure for a living party is a "
                + "data-subject request (Wave 6), not this operation.")
            .RequirePermission(PartyPermissions.PartyAnonymise);

        parties.MapGet("/{id:guid}/consents", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetConsents(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ConsentView>>(StatusCodes.Status200OK)
            .WithName("PartyGetConsents")
            .WithSummary("The party's current consent per purpose")
            .RequirePermission(PartyPermissions.ConsentRead);

        parties.MapPost("/{id:guid}/consents/grant", async (Guid id, ConsentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new GrantConsent(id, r.PurposeCode, r.CaptureChannel, r.EvidenceRef), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ConsentView>>(StatusCodes.Status200OK)
            .WithName("PartyGrantConsent")
            .WithSummary("Record a consent; the ledger is append-only (PTY-005)")
            .RequirePermission(PartyPermissions.ConsentUpdate);

        parties.MapPost("/{id:guid}/consents/withdraw", async (Guid id, ConsentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new WithdrawConsent(id, r.PurposeCode, r.CaptureChannel, r.EvidenceRef), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<ConsentView>>(StatusCodes.Status200OK)
            .WithName("PartyWithdrawConsent")
            .WithSummary("Record a withdrawal of consent")
            .RequirePermission(PartyPermissions.ConsentUpdate);

        api.MapGroup("/party/merges").WithTags(Name).MapPost("/", async (MergePartiesRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new MergeParties(r.SurvivorId, r.MergedId, r.Reason), ct)).ToHttp(id => Results.Ok(new MergedParties(id, r.SurvivorId))))
            .Produces<MergedParties>(StatusCodes.Status200OK)
            .WithName("PartyMergeParties")
            .WithSummary("Merge a duplicate party into its survivor")
            .WithDescription("The merged party becomes read-only and points to the survivor; the merge is kept in an auditable history (PTY-004).")
            .RequirePermission(PartyPermissions.PartyMerge)
            .RequireIdempotencyKey();

        var portal = api.MapGroup("/portal/party").WithTags(Name);
        portal.MapGet("/parties", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    if (!Cursor.TryDecode(cursor, out var after))
                    {
                        return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                    }

                    var size = Cursor.ClampLimit(limit);
                    return (await dispatcher.Query(new ListPortalClients(after, size), ct)).ToHttp(items =>
                        Results.Ok(new CursorPage<PortalClientView>(items, items.Count == size ? Cursor.Encode(items[^1].Id) : null)));
                })
            .Produces<CursorPage<PortalClientView>>(StatusCodes.Status200OK)
            .WithName("PortalPartyListClients")
            .WithSummary("The broker's clients, by the caller's link only (BR-091)")
            .WithDescription("Parties the broker or a sub-broker registered, and those who hold, or are quoted or proposed for, a policy under their codes. "
                + "A registration linked to an existing party after review is shown as that party; anonymised parties are not shown.")
            .RequirePermission(PartyPermissions.PartyRead)
            .ForPortal(BuildingBlocks.Application.Context.ExternalSubject.Intermediary);
        portal.MapGet("/parties/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetPortalClient(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalClientView>(StatusCodes.Status200OK)
            .WithName("PortalPartyGetClient")
            .WithSummary("A client of the broker's (BR-091)")
            .RequirePermission(PartyPermissions.PartyRead)
            .ForPortal(BuildingBlocks.Application.Context.ExternalSubject.Intermediary);
        portal.MapPost("/parties", async (RegisterPartyThroughPortal r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(r, ct)).ToHttp(v => Results.Accepted($"/api/v1/portal/party/parties/{v.Id}", v)))
            .Produces<PortalRegistration>(StatusCodes.Status202Accepted)
            .WithName("PortalPartyRegister")
            .WithSummary("A broker registers a new client")
            .WithDescription("External-user access §6: the answer is the same whether or not the person matches an existing party (POPIA). A match goes to "
                + "staff review (PARTY_MATCH_REVIEW) instead of a duplicate error.")
            .RequirePermission(PartyPermissions.PartyCreate)
            .RequireIdempotencyKey()
            .ForPortal(BuildingBlocks.Application.Context.ExternalSubject.Intermediary);
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

    /// <summary>Legal holds on the party search access log (OQ-073): held entries are not deleted by retention.</summary>
    private static void MapSearchLogHolds(RouteGroupBuilder holds)
    {
        holds.MapPost("/", async (SearchLogHoldRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new PlaceSearchLogHold(r.SearchedBy, r.Reason), ct)).ToHttp(id => Results.Created($"/api/v1/party/search-log/holds/{id}", new { id })))
            .Produces<CreatedId>(StatusCodes.Status201Created)
            .WithName("PartyPlaceSearchLogHold")
            .WithSummary("Place a legal hold on the party search log: one searcher's entries (searchedBy) or the whole log")
            .RequirePermission(PartyPermissions.SearchLogHold)
            .RequireIdempotencyKey();

        holds.MapGet("/", async (IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new ListSearchLogHolds(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<SearchLogHoldView>>(StatusCodes.Status200OK)
            .WithName("PartyListSearchLogHolds")
            .WithSummary("The legal holds on the party search log, active and released")
            .RequirePermission(Audit.Contracts.AuditPermissions.Read);

        holds.MapPost("/{id:guid}/release", async (Guid id, SearchLogHoldReleaseRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RequestSearchLogHoldRelease(id, r.Reason), ct)).ToHttp(view => Results.Accepted(null, view)))
            .Produces<SearchLogHoldView>(StatusCodes.Status202Accepted)
            .WithName("PartyReleaseSearchLogHold")
            .WithSummary("Ask for a legal hold to be released; a second person approves it (LEGAL_HOLD_RELEASE)")
            .WithDescription("Releasing a hold needs a second person (OQ-111). The request is recorded on the hold and waits for a LEGAL_HOLD_RELEASE approval; "
                + "the hold stays in force until it is approved. A rejected, expired or withdrawn approval clears the request, and the hold stays (its "
                + "latestRejection says how it ended). 409 INVALID_STATE_TRANSITION when a release already waits for approval or the hold is released.")
            .RequirePermission(PartyPermissions.SearchLogHold);
    }
}

public sealed record SearchLogHoldRequest(string? SearchedBy, string Reason);

/// <param name="Reason">Why the hold may be released (at most 200 characters); the checker sees it.</param>
public sealed record SearchLogHoldReleaseRequest([property: System.ComponentModel.DataAnnotations.MinLength(1), System.ComponentModel.DataAnnotations.MaxLength(200)] string Reason);
