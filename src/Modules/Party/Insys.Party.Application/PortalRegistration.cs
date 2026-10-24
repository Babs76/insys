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
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

public sealed record PortalIdentifier(string DocType, string Country, string Number);

/// <summary>
/// Command RegisterPartyThroughPortal (external-user access §3, §6): a broker registers a new client. A match with an
/// existing party is never answered with a duplicate error: the registration is received either way, and a match goes to
/// staff review, so the broker cannot learn that someone is already a client (POPIA).
/// </summary>
public sealed record RegisterPartyThroughPortal(IndividualData Individual, PortalIdentifier? Identifier) : ICommand<PortalRegistration>;

/// <summary>The same answer whether or not the person matched someone: the new party's ID, received.</summary>
public sealed record PortalRegistration(Guid Id, string Status);

internal sealed class RegisterPartyThroughPortalHandler(IPartyRepository parties, IPartyCodes codes, IFieldProtector protector, IExecutionContext execution,
    IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<RegisterPartyThroughPortal, PortalRegistration>
{
    public async Task<Result<PortalRegistration>> Handle(RegisterPartyThroughPortal command, CancellationToken cancellationToken)
    {
        var who = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        ProtectedValue? number = null;
        if (command.Identifier is { } identifier)
        {
            var (exists, pattern) = await codes.IdentityDocumentType(identifier.Country, identifier.DocType, businessDate.Today, cancellationToken);
            var value = identifier.Number.Trim();
            if (!exists || (!string.IsNullOrEmpty(pattern) && !Regex.IsMatch(value, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromMilliseconds(100))))
            {
                return Error.Validation(PartyErrorCodes.IdentifierInvalid, $"The {identifier.DocType} number is not valid for {identifier.Country}.");
            }

            number = protector.Protect(value);
        }

        var matched = await parties.Match(command.Individual,
            command.Identifier is { } i && number is { } n ? (i.DocType, i.Country, n.Hash) : null, cancellationToken);
        var id = Guid.CreateVersion7();
        var party = Domain.Parties.Party.RegisterIndividual(id, command.Individual, clock.UtcNow);
        party.RegisteredThroughPortal($"{who.Kind}:{(who.Kind == ExternalSubject.Intermediary ? who.IntermediaryCode : who.PartyId?.ToString() ?? who.ProviderId)}",
            matched, clock.UtcNow);

        // A matched party's document stays with the existing party; the review links the two.
        if (matched is null && command.Identifier is { } document && number is { } protectedNumber)
        {
            party.AddIdentifier(document.DocType, document.Country, protectedNumber.Encrypted, protectedNumber.Hash, clock.UtcNow);
        }

        parties.Add(party);
        audit.Record(new AuditEntry("PARTY", "RegisterPartyThroughPortal", nameof(Domain.Parties.Party), id.ToString(),
            new Dictionary<string, string?> { ["review"] = matched is null ? "no" : "yes" }));
        return new PortalRegistration(id, "Received");
    }
}
