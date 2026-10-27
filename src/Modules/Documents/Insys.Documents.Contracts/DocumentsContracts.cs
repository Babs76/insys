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

/// <summary>Approvals the Documents module asks Workflow for (ADR-009).</summary>
public static class DocumentApprovals
{
    /// <summary>A document's legal hold; the business reference is the hold.</summary>
    public const string LegalHoldBusinessRefType = "DOCUMENT_LEGAL_HOLD";

    /// <summary>A legal hold on a record itself (D-34): a party, policy, membership, claim or case; the business reference is the hold.</summary>
    public const string RecordHoldBusinessRefType = "RECORD_LEGAL_HOLD";

    /// <summary>Releasing a legal hold needs a second person (OQ-111, 25 October 2026); the compliance officer is the checker.</summary>
    public const string LegalHoldReleaseType = "LEGAL_HOLD_RELEASE";
}

/// <summary>A legal hold in force on a document of a record, or on the record itself.</summary>
/// <param name="HoldId">The hold, or null for a document flagged as held with no hold row in force.</param>
/// <param name="DocumentId">The held document, or null for a hold on the record itself.</param>
public sealed record RecordHold(Guid? HoldId, Guid? DocumentId, string RecordType, Guid RecordId, string? ReasonCode);

/// <summary>
/// Public query interface (D-34): the legal holds in force on records, read on the Documents module's own role, for the
/// party anonymisation check: holds on the records' documents, and holds on the records themselves (document.record_hold).
/// </summary>
public interface IRecordHolds
{
    /// <summary>The legal holds in force on the records (by ID), or on a document whose business reference is one of them.</summary>
    Task<IReadOnlyList<RecordHold>> Holds(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}

[IntegrationEvent("document.DocumentGenerated")]
public sealed record DocumentGenerated(Guid DocumentId, string CategoryCode, string BusinessRefType, Guid BusinessRefId, string? TemplateCode, int? TemplateVersion)
    : IIntegrationEvent;

[IntegrationEvent("document.DocumentDelivered")]
public sealed record DocumentDelivered(Guid DocumentId, string Channel) : IIntegrationEvent;

[IntegrationEvent("document.DocumentDisposed")]
public sealed record DocumentDisposed(Guid DocumentId) : IIntegrationEvent;
