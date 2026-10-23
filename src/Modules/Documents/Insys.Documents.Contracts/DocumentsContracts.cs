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
using Insys.BuildingBlocks.Contracts;

namespace Insys.Documents.Contracts;

public static class DocumentPermissions
{
    public const string DocumentRead = "document.read";
    public const string DocumentCreate = "document.create";
    public const string DocumentDeliver = "document.deliver";
    public const string DocumentDispose = "document.dispose";
    public const string LegalHoldManage = "legal_hold.manage";
}

[IntegrationEvent("document.DocumentGenerated")]
public sealed record DocumentGenerated(Guid DocumentId, string CategoryCode, string BusinessRefType, Guid BusinessRefId, string? TemplateCode, int? TemplateVersion)
    : IIntegrationEvent;

[IntegrationEvent("document.DocumentDelivered")]
public sealed record DocumentDelivered(Guid DocumentId, string Channel) : IIntegrationEvent;

[IntegrationEvent("document.DocumentDisposed")]
public sealed record DocumentDisposed(Guid DocumentId) : IIntegrationEvent;
