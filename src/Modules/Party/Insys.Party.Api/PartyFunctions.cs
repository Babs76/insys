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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Party.Api;

/// <summary>
/// The Party module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class PartyFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("PARTIES", "PARTIES", "Register people & organisations", "party/parties", "PartyGetParty")
        .Action("PARTY_SEARCH_PARTIES", "PARTIES", "PartySearchParties")
        .Action("PARTY_REGISTER_PARTY", "PARTIES", "PartyRegisterParty")
        .Action("PARTY_UPDATE_INDIVIDUAL", "PARTIES", "PartyUpdateIndividual")
        .Action("PARTY_CHANGE_PREFERRED_LANGUAGE", "PARTIES", "PartyChangePreferredLanguage")
        .Action("PARTY_UPDATE_ORGANISATION", "PARTIES", "PartyUpdateOrganisation")
        .Action("PARTY_ADD_ADDRESS", "PARTIES", "PartyAddAddress")
        .Action("PARTY_ADD_CONTACT_POINT", "PARTIES", "PartyAddContactPoint")
        .Action("PARTY_ADD_IDENTIFIER", "PARTIES", "PartyAddIdentifier")
        .Action("PARTY_VERIFY_IDENTIFIER", "PARTIES", "PartyVerifyIdentifier")
        .Action("PARTY_RECORD_RELATIONSHIP", "PARTIES", "PartyRecordRelationship")
        .Action("PARTY_ASSIGN_ROLE", "PARTIES", "PartyAssignRole")
        .Action("PARTY_RECORD_DEATH", "PARTIES", "PartyRecordDeath")
        .Action("PARTY_RESOLVE_MATCH", "PARTIES", "PartyResolveMatch")
        .Action("PARTY_ANONYMISE", "PARTIES", "PartyAnonymise", privileged: true)
        .Screen("PARTY_SEARCH_LOG", "PARTIES", "Search with access log", "party/search-log", "PartyListSearchLogHolds", "PartyListSearches")
        .Action("PARTY_PLACE_SEARCH_LOG_HOLD", "PARTY_SEARCH_LOG", "PartyPlaceSearchLogHold", privileged: true)
        .Action("PARTY_RELEASE_SEARCH_LOG_HOLD", "PARTY_SEARCH_LOG", "PartyReleaseSearchLogHold", privileged: true)
        .Action("PARTY_MERGE_PARTIES", "PARTIES", "PartyMergeParties", privileged: true)
        .Screen("PARTY_BANK_ACCOUNTS", "PARTIES", "Bank accounts", "party/bank-accounts", "PartyListBankAccounts")
        .Action("PARTY_ADD_BANK_ACCOUNT", "PARTY_BANK_ACCOUNTS", "PartyAddBankAccount")
        .Permission("PARTY_BANK_ACCOUNT_VERIFY", "PARTY_BANK_ACCOUNTS", "bank_account.verify")
        .Screen("PARTY_CONSENTS", "CONSENT_PREFERENCES", "Consent", "party/consents", "PartyGetConsents")
        .Action("PARTY_GRANT_CONSENT", "PARTY_CONSENTS", "PartyGrantConsent")
        .Action("PARTY_WITHDRAW_CONSENT", "PARTY_CONSENTS", "PartyWithdrawConsent")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_PARTY_PARTIES", "BROKER_PORTAL", "Clients", "portal/party/parties", "PortalPartyListClients", "PortalPartyGetClient")
        .Action("BROKER_PORTAL_PARTY_REGISTER", "BROKER_PORTAL_PARTY_PARTIES", "PortalPartyRegister")
            .Functions,
    ];
}
