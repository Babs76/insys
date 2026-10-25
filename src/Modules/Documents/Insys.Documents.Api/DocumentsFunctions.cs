/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Documents
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

namespace Insys.Documents.Api;

/// <summary>
/// The Documents module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class DocumentsFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("DOCUMENTS", "DOCUMENTS", "Storage", "documents/documents", "DocumentsList", "DocumentsGet", "DocumentsReadContent")
        .Action("DOCUMENTS_GENERATE", "DOCUMENTS", "DocumentsGenerate")
        .Action("DOCUMENTS_STORE", "DOCUMENTS", "DocumentsStore")
        .Action("DOCUMENTS_DELIVER", "DOCUMENTS", "DocumentsDeliver")
        .Action("DOCUMENTS_PLACE_LEGAL_HOLD", "DOCUMENTS", "DocumentsPlaceLegalHold")
        .Action("DOCUMENTS_RELEASE_LEGAL_HOLD", "DOCUMENTS", "DocumentsReleaseLegalHold")
        .Action("DOCUMENTS_DISPOSE", "DOCUMENTS", "DocumentsDispose")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_DOCUMENTS_LIST", "CUSTOMER_PORTAL", "Documents", "portal/documents", "PortalDocumentsList")
        .Screen("CUSTOMER_PORTAL_DOCUMENTS_CONTENT", "CUSTOMER_PORTAL", "Documents", "portal/documents/content", "PortalDocumentsReadContent")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_DOCUMENTS_LIST", "BROKER_PORTAL", "Documents", "portal/documents", "PortalDocumentsList")
        .Screen("BROKER_PORTAL_DOCUMENTS_CONTENT", "BROKER_PORTAL", "Documents", "portal/documents/content", "PortalDocumentsReadContent")
            .Functions,
        .. new FunctionMap([Audiences.Provider], Features.ProviderPortal)
        .Screen("PROVIDER_PORTAL_DOCUMENTS_LIST", "PROVIDER_PORTAL", "Documents", "portal/documents", "PortalDocumentsList")
        .Screen("PROVIDER_PORTAL_DOCUMENTS_CONTENT", "PROVIDER_PORTAL", "Documents", "portal/documents/content", "PortalDocumentsReadContent")
            .Functions,
    ];
}
