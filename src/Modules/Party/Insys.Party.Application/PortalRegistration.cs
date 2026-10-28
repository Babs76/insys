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
using Insys.BuildingBlocks.Contracts;
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
            party.AddIdentifier(document.DocType, document.Country, protectedNumber.Encrypted, protectedNumber.Hash, IdentifierView.LastFour(document.Number.Trim()),
                clock.UtcNow);
        }

        parties.Add(party);
        audit.Record(new AuditEntry("PARTY", "RegisterPartyThroughPortal", nameof(Domain.Parties.Party), id.ToString(),
            new Dictionary<string, string?> { ["review"] = matched is null ? "no" : "yes" }));
        return new PortalRegistration(id, "Received");
    }
}

/// <summary>
/// Command ResolvePartyMatch (external-user access §6), by staff: link a matched portal registration to the party it
/// matched, or confirm it as a new party. A second person approves the proposal in Workflow (PARTY_MATCH_RESOLUTION,
/// SRS-002); only then is it applied. <paramref name="ExistingPartyId"/> is needed only for registrations made before
/// the match was recorded.
/// </summary>
public sealed record ResolvePartyMatch(Guid PartyId, string Resolution, Guid? ExistingPartyId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

internal sealed class ResolvePartyMatchHandler(IPartyRepository parties, IExecutionContext execution, IClock clock, IAuditTrail audit) : ICommandHandler<ResolvePartyMatch, long>
{
    public async Task<Result<long>> Handle(ResolvePartyMatch command, CancellationToken cancellationToken)
    {
        var user = Parties.User(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (command.Resolution is not ("LINK" or "NEW"))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The resolution is LINK or NEW.");
        }

        audit.Record(new AuditEntry("PARTY", "ResolvePartyMatch", nameof(Domain.Parties.Party), command.PartyId.ToString(),
            new Dictionary<string, string?> { ["resolution"] = command.Resolution }));
        return await Parties.Change(parties, command.PartyId, command.ExpectedVersion,
            p => p.ProposeMatchResolution(command.Resolution == "LINK", command.ExistingPartyId, user.Value, clock.UtcNow), cancellationToken);
    }
}

/// <summary>
/// Workflow's decision on a party-match resolution. Approved: a link merges the registration into the existing party
/// (PTY-004, with its merge history), and a new party is confirmed. Rejected: the registration goes back to review.
/// Workflow has checked that the checker is not the maker. A redelivered decision already applied changes nothing; one
/// the registration will not take parks the delivery (D-5, ADR-022): an ending as APPROVAL_ENDING_NOT_APPLIED, and a
/// grant (no resolution waiting any more, or the matched party merged, anonymised or gone since) as APPROVAL_NOT_APPLIED,
/// which Workflow marks not applied and tells the requester.
/// </summary>
public sealed class PartyMatchResolutionPolicy(IPartyRepository parties, IClock clock, IAuditTrail audit) :
    IIntegrationEventHandler<Workflow.Contracts.ApprovalGranted>,
    IIntegrationEventHandler<Workflow.Contracts.ApprovalRejected>
{
    public Task Handle(Workflow.Contracts.ApprovalGranted granted, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(granted.BusinessRefType, granted.BusinessRefId, true, granted.ApprovalId, granted.CheckerId, cancellationToken);

    public Task Handle(Workflow.Contracts.ApprovalRejected rejected, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Decide(rejected.BusinessRefType, rejected.BusinessRefId, false, rejected.ApprovalId, rejected.CheckerId, cancellationToken);

    private async Task Decide(string businessRefType, Guid partyId, bool approved, Guid approvalId, Guid checkerId, CancellationToken cancellationToken)
    {
        if (businessRefType != Contracts.PartyApprovals.MatchBusinessRefType || await parties.Get(partyId, cancellationToken) is not { } registration)
        {
            return;
        }

        // Every ending other than approval (rejected, expired, withdrawn) sends it back to review; one already back there,
        // or a grant already applied (linked or confirmed new), is a redelivery.
        if (approved ? registration.ReviewStatus is "LINKED" or "CONFIRMED_NEW" : registration.ReviewStatus == Domain.Parties.Party.PendingReview)
        {
            return;
        }

        var decided = registration.DecideMatchResolution(approved, clock.UtcNow);
        if (decided.IsFailure)
        {
            throw NotApplied(approved, approvalId);
        }

        if (approved && registration.MatchResolution == "LINK")
        {
            // The party it matched was merged, anonymised or removed since the resolution was proposed: it will not take the link.
            var snapshot = System.Text.Json.JsonSerializer.Serialize(new { source = "PORTAL", registeredBy = registration.RegisteredBy, approval = approvalId });
            if (await parties.Get(registration.MatchedPartyId!.Value, cancellationToken) is not { } existing
                || registration.MergeInto(existing, checkerId, "Portal registration linked to the party it matched, after review", snapshot, clock.UtcNow).IsFailure)
            {
                throw NotApplied(approved, approvalId);
            }
        }

        audit.Record(new AuditEntry("PARTY", approved ? "ApprovePartyMatchResolution" : "RejectPartyMatchResolution", nameof(Domain.Parties.Party), partyId.ToString(),
            new Dictionary<string, string?> { ["approval"] = approvalId.ToString(), ["resolution"] = registration.MatchResolution }));
    }

    private static PrerequisiteMissingException NotApplied(bool approved, Guid approvalId) =>
        new(approved ? PrerequisiteCodes.ApprovalNotApplied : PrerequisiteCodes.ApprovalEndingNotApplied, $"{Contracts.PartyApprovals.MatchResolutionType}:{approvalId}");
}
