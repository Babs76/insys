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
using FluentValidation;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

/// <summary>An identifier to search by: an exact number of a document type and country, matched by keyed hash.</summary>
public sealed record IdentifierFilter([property: CodeList(Tables.IdentityDocuments)] string DocType, [property: CodeList(Tables.Countries)] string Country, string Number);

/// <summary>
/// Command SearchParties (GAP-022): find parties by name (at least 3 characters, anywhere in the display name), an exact
/// identifier, a party number (BR-029), a role in effect, and status. A command, not a query, because every search is written to the access log
/// (PTY-003, PTY-005): who searched, when, the filters and the number of results; an identifier by its hash only.
/// </summary>
public sealed record SearchParties(string? Name, IdentifierFilter? Identifier, string? RoleCode, PartyStatus? Status, Guid? AfterId, int Limit,
    string? PartyNumber = null) : ICommand<IReadOnlyList<PartySummary>>;

public sealed class SearchPartiesValidator : AbstractValidator<SearchParties>
{
    public SearchPartiesValidator()
    {
        RuleFor(s => s).Must(s => s.Name is not null || s.Identifier is not null || s.RoleCode is not null || s.Status is not null || s.PartyNumber is not null)
            .WithMessage("Give at least one of name, identifier, partyNumber, roleCode or status.");
        RuleFor(s => s.PartyNumber).Must(n => PartyNumbers.IsValid(n!.Trim().ToUpperInvariant())).When(s => s.PartyNumber is not null)
            .WithMessage("partyNumber is P, 8 digits and a check digit (for example P000000018).");
        RuleFor(s => s.Name).Must(n => n!.Trim().Length >= 3).When(s => s.Name is not null).WithMessage("name needs at least 3 characters.")
            .MaximumLength(200);
        RuleFor(s => s.Identifier!.DocType).NotEmpty().MaximumLength(15).When(s => s.Identifier is not null);
        RuleFor(s => s.Identifier!.Country).Length(2).When(s => s.Identifier is not null);
        RuleFor(s => s.Identifier!.Number).NotEmpty().MaximumLength(80).When(s => s.Identifier is not null);
        RuleFor(s => s.RoleCode).MaximumLength(20);
    }
}

/// <summary>
/// A party in search results: only what is needed to pick the right one (PTY-005). Open the party for its details;
/// identifiers, birth date and contact details are not in results.
/// </summary>
public sealed record PartySummary(Guid Id, string PartyNumber, PartyKind Kind, string DisplayName, PartyStatus Status, IReadOnlyList<string> Roles);

/// <summary>A party search in the access log, for audit roles (GAP-022): the identifier as document type, country and keyed hash only.</summary>
public sealed record PartySearchLogEntry(Guid Id, DateTimeOffset SearchedAt, string SearchedBy, string? NameFilter, string? IdentifierDocType,
    string? IdentifierCountry, string? IdentifierHash, string? RoleFilter, string? StatusFilter, int ResultCount, string? PartyNumberFilter = null);

/// <summary>Query ListPartySearches: the access log, newest first, by who searched and when.</summary>
public sealed record ListPartySearches(string? SearchedBy, DateTimeOffset? From, DateTimeOffset? To, Guid? BeforeId, int Limit)
    : IQuery<IReadOnlyList<PartySearchLogEntry>>;

public interface IPartySearch
{
    /// <summary>Parties matching every given filter, by ID after <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<PartySummary>> Search(string? name, (string DocType, string Country, string Hash)? identifier, string? roleCode, PartyStatus? status,
        DateOnly today, Guid? afterId, int limit, CancellationToken cancellationToken, string? partyNumber = null);

    void Log(PartySearchLog entry);

    Task<IReadOnlyList<PartySearchLogEntry>> Searches(ListPartySearches query, CancellationToken cancellationToken);
}

internal sealed class ListPartySearchesHandler(IPartySearch search) : IQueryHandler<ListPartySearches, IReadOnlyList<PartySearchLogEntry>>
{
    public async Task<Result<IReadOnlyList<PartySearchLogEntry>>> Handle(ListPartySearches query, CancellationToken cancellationToken) =>
        Result.Success(await search.Searches(query, cancellationToken));
}

internal sealed class SearchPartiesHandler(IPartySearch search, IFieldProtector protector, IExecutionContext execution, IBusinessDate businessDate, IClock clock)
    : ICommandHandler<SearchParties, IReadOnlyList<PartySummary>>
{
    public async Task<Result<IReadOnlyList<PartySummary>>> Handle(SearchParties command, CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim();
        (string DocType, string Country, string Hash)? identifier = command.Identifier is { } i
            ? (i.DocType, i.Country.ToUpperInvariant(), protector.Hash(i.Number.Trim()))
            : null;
        var number = command.PartyNumber?.Trim().ToUpperInvariant();
        var results = await search.Search(name, identifier, command.RoleCode, command.Status, businessDate.Today, command.AfterId, command.Limit, cancellationToken, number);
        search.Log(PartySearchLog.Record(execution.ActorId, name, identifier, command.RoleCode, command.Status?.ToString(), results.Count, clock.UtcNow, number));
        return Result.Success(results);
    }
}
