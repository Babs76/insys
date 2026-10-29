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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Contracts;
using Insys.Integration.Contracts;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

/// <summary>
/// F001 (addenda/due-diligence.md §4.1, signed off 29 October 2026): an identity document added to a party, by staff or
/// through a portal, is checked with the tenant's active identity verification provider. The answer is a due diligence
/// check; a pass also marks the identifier verified with the provider's reference. With no provider active, nothing is
/// sent: verification is by a person, against a document (VerifyIdentifier). The number is decrypted only to be sent and
/// is never logged, kept in the check or put in an event.
/// </summary>
public sealed class IdentityVerificationPolicy(IPartyRepository parties, IPartnerDirectory partners, IIdentityVerificationAdapter adapter, IFieldProtector protector,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit, DueDiligenceService dueDiligence) : IIntegrationEventHandler<Contracts.PartyIdentifierAdded>
{
    public async Task Handle(Contracts.PartyIdentifierAdded added, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (await partners.ActivePartner(tenantId, PartnerTypes.IdentityVerification, businessDate.Today, cancellationToken) is not { } partner
            || await parties.Get(added.PartyId, cancellationToken) is not { } party
            || party.Identifiers.SingleOrDefault(i => i.Id == added.IdentifierId) is not { VerifiedAt: null } identifier)
        {
            return;
        }

        var result = await adapter.Verify(tenantId, partner, identifier.DocType, identifier.Country, protector.Unprotect(identifier.NumberEnc),
            party.Individual?.FirstNames, party.Individual?.Surname, party.Individual?.BirthDate, cancellationToken);
        var check = DueDiligenceCheck.ByProvider(party.Id, identifier.Id, partner, result.Verified, result.ProviderRef, result.Reason, clock.UtcNow);
        parties.Add(check);
        if (result.Verified && result.ProviderRef is { } reference)
        {
            var verified = party.VerifyIdentifier(identifier.Id, reference, clock.UtcNow);
            if (verified.IsFailure)
            {
                // A merged or anonymised party is read-only: the check stands, the identifier stays as it is.
                audit.Record(new AuditEntry("PARTY", "VerifyIdentifierSkipped", nameof(Domain.Parties.Party), party.Id.ToString(),
                    new Dictionary<string, string?> { ["code"] = verified.Error!.Code }));
            }
        }

        audit.Record(new AuditEntry("PARTY", "VerifyIdentity", nameof(Domain.Parties.Party), party.Id.ToString(), new Dictionary<string, string?>
        {
            ["identifier"] = identifier.Id.ToString(), ["partner"] = partner, ["outcome"] = result.Verified ? DueDiligenceCheck.Passed : DueDiligenceCheck.Failed,
        }));

        // F002: the identity check may complete what the requirement sets need.
        await dueDiligence.Reassess(party, await dueDiligence.For(party.Id, cancellationToken), null, [check], cancellationToken);
    }
}
