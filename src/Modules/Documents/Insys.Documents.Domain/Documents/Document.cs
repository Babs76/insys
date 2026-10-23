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
using Insys.BuildingBlocks.Domain;

namespace Insys.Documents.Domain.Documents;

/// <summary>States from domain-model.json (document › Document) after rendering; Requested is the generation request.</summary>
public enum DocumentStatus
{
    Generated,
    Delivered,
    Disposed,
}

public enum DocumentTrigger
{
    DeliverDocument,
    RetentionEndedNoHold,
}

/// <summary>Classification, from the document category (V0002 record classes).</summary>
public static class Classifications
{
    /// <summary>Access to these is recorded (DOC-003).</summary>
    public static bool IsSensitive(string classification) => classification is "CONFIDENTIAL" or "PERSONAL" or "HIGHLY_SENSITIVE";

    /// <summary>These also need one of the category's allowed roles (DOC-004).</summary>
    public static bool NeedsAllowedRole(string classification) => classification == "HIGHLY_SENSITIVE";
}

/// <summary>Where the content is kept, and how to check it is intact.</summary>
public sealed record StoredContent(string StorageUri, string MimeType, long SizeBytes, string Sha256);

/// <summary>TemplateVersionRef: the template version a document was generated from (DOC-002).</summary>
public sealed record TemplateVersionRef(string TemplateCode, int Version);

public sealed record DocumentGenerated(Guid DocumentId, string CategoryCode, string BusinessRefType, Guid BusinessRefId, string? TemplateCode, int? TemplateVersion,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record DocumentDelivered(Guid DocumentId, string Channel, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record DocumentDisposed(Guid DocumentId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Entity Delivery (document.delivery).</summary>
public sealed class Delivery : Entity<Guid>, ITenantScoped
{
    private Delivery()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public string Channel { get; private set; } = "";

    public string Recipient { get; private set; } = "";

    /// <summary>SENT or FAILED.</summary>
    public string Status { get; private set; } = "";

    public string? ProviderRef { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public long RowVersion { get; private set; }

    internal static Delivery Create(Guid documentId, string channel, string recipient, bool sent, string? providerRef, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentId = documentId,
        Channel = channel,
        Recipient = recipient,
        Status = sent ? "SENT" : "FAILED",
        ProviderRef = providerRef,
        SentAt = sent ? now : null,
    };
}

/// <summary>Entity LegalHold (document.legal_hold, scope DOCUMENT).</summary>
public sealed class LegalHold : Entity<Guid>, ITenantScoped
{
    public const string DocumentScope = "DOCUMENT";

    private LegalHold()
    {
    }

    public Guid TenantId { get; private set; }

    public string ScopeType { get; private set; } = DocumentScope;

    public Guid ScopeId { get; private set; }

    public string ReasonCode { get; private set; } = "";

    public Guid PlacedBy { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public long RowVersion { get; private set; }

    public bool IsActive => ReleasedAt is null;

    internal static LegalHold Place(Guid documentId, string reasonCode, Guid placedBy, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), ScopeId = documentId, ReasonCode = reasonCode, PlacedBy = placedBy, PlacedAt = now };

    internal void Release(DateTimeOffset now) => ReleasedAt = now;
}

/// <summary>Entity AccessRecord (document.document_access_log): one read of a sensitive document (DOC-003). Never changed.</summary>
public sealed class AccessRecord : Entity<Guid>, ITenantScoped
{
    private AccessRecord()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid AccessorId { get; private set; }

    public string Action { get; private set; } = "";

    public string Purpose { get; private set; } = "";

    public DateTimeOffset AccessedAt { get; private set; }

    public static AccessRecord Create(Guid documentId, Guid accessorId, string action, string purpose, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        DocumentId = documentId,
        AccessorId = accessorId,
        Action = action,
        Purpose = Guard.MaxLength(Guard.NotNullOrWhiteSpace(purpose), 60),
        AccessedAt = now,
    };
}

/// <summary>document.generation_request: the Requested state of a generated document, with a reference to its data (not the data).</summary>
public sealed class GenerationRequest : Entity<Guid>, ITenantScoped
{
    private GenerationRequest()
    {
    }

    public Guid TenantId { get; private set; }

    public string TemplateCode { get; private set; } = "";

    public int TemplateVersion { get; private set; }

    public string Language { get; private set; } = "";

    /// <summary>JSON: the business reference and the merge field names supplied, never their values.</summary>
    public string DataRef { get; private set; } = "{}";

    /// <summary>GENERATED once rendered and stored.</summary>
    public string Status { get; private set; } = "";

    public Guid? DocumentId { get; private set; }

    public string? Error { get; private set; }

    public long RowVersion { get; private set; }

    public static GenerationRequest Generated(TemplateVersionRef template, string language, string dataRef, Guid documentId) => new()
    {
        Id = Guid.CreateVersion7(),
        TemplateCode = template.TemplateCode,
        TemplateVersion = template.Version,
        Language = language,
        DataRef = dataRef,
        Status = "GENERATED",
        DocumentId = documentId,
    };
}

/// <summary>
/// The Document aggregate: kept in tenant-scoped storage (DOC-001), with the template and product versions it was
/// generated from (DOC-002); access to sensitive documents is recorded (DOC-003); not disposed of before its retention
/// date or while on legal hold (§18.1).
/// </summary>
public sealed class Document : AggregateRoot<Guid>
{
    public static readonly StateMachine<DocumentStatus, DocumentTrigger> Lifecycle = new StateMachine<DocumentStatus, DocumentTrigger>()
        .Allow(DocumentStatus.Generated, DocumentTrigger.DeliverDocument, DocumentStatus.Delivered)
        .Allow(DocumentStatus.Delivered, DocumentTrigger.DeliverDocument, DocumentStatus.Delivered)
        .Allow(DocumentStatus.Generated, DocumentTrigger.RetentionEndedNoHold, DocumentStatus.Disposed)
        .Allow(DocumentStatus.Delivered, DocumentTrigger.RetentionEndedNoHold, DocumentStatus.Disposed);

    private readonly List<Delivery> _deliveries = [];
    private readonly List<LegalHold> _holds = [];

    private Document()
    {
    }

    public string CategoryCode { get; private set; } = "";

    public string Classification { get; private set; } = "";

    public string StorageUri { get; private set; } = "";

    public string MimeType { get; private set; } = "";

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = "";

    public string? TemplateCode { get; private set; }

    public int? TemplateVersion { get; private set; }

    /// <summary>The product version the document belongs to, such as "GEN-MOTOR-PV v4" (DOC-002).</summary>
    public string? ProductVersionRef { get; private set; }

    public string BusinessRefType { get; private set; } = "";

    public Guid BusinessRefId { get; private set; }

    /// <summary>Null until the retention trigger is known; a document without a retention date is never disposed of.</summary>
    public DateOnly? RetentionUntil { get; private set; }

    public bool LegalHold { get; private set; }

    public DocumentStatus Status { get; private set; }

    public DateTimeOffset? DisposedAt { get; private set; }

    public IReadOnlyList<Delivery> Deliveries => _deliveries;

    public IReadOnlyList<LegalHold> Holds => _holds;

    /// <summary>Commands GenerateDocument (with a template) and StoreDocument (without): the rendered or uploaded content is already stored.</summary>
    public static Document Create(Guid id, string categoryCode, string classification, StoredContent content, TemplateVersionRef? template, string? productVersionRef,
        string businessRefType, Guid businessRefId, DateOnly? retentionUntil, DateTimeOffset now)
    {
        var document = new Document
        {
            Id = Guard.NotEmpty(id),
            CategoryCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(categoryCode), 20),
            Classification = classification,
            StorageUri = Guard.MaxLength(content.StorageUri, 300),
            MimeType = Guard.MaxLength(content.MimeType, 80),
            SizeBytes = content.SizeBytes,
            Sha256 = content.Sha256,
            TemplateCode = template?.TemplateCode,
            TemplateVersion = template?.Version,
            ProductVersionRef = productVersionRef is null ? null : Guard.MaxLength(productVersionRef, 30),
            BusinessRefType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(businessRefType), 30),
            BusinessRefId = Guard.NotEmpty(businessRefId),
            RetentionUntil = retentionUntil,
            Status = DocumentStatus.Generated,
        };
        document.Raise(new DocumentGenerated(id, document.CategoryCode, document.BusinessRefType, businessRefId, template?.TemplateCode, template?.Version, now));
        document.MarkChanged();
        return document;
    }

    /// <summary>Command DeliverDocument: every attempt is recorded; a sent delivery moves the document to Delivered.</summary>
    public Result<Delivery> DeliverDocument(string channel, string recipient, bool sent, string? providerRef, DateTimeOffset now)
    {
        if (Status == DocumentStatus.Disposed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The document was disposed of.");
        }

        var delivery = Delivery.Create(Id, channel, Guard.MaxLength(Guard.NotNullOrWhiteSpace(recipient), 200), sent, providerRef, now);
        _deliveries.Add(delivery);
        MarkChanged();
        if (sent && Fire(DocumentTrigger.DeliverDocument).IsSuccess)
        {
            Raise(new DocumentDelivered(Id, channel, now));
        }

        return delivery;
    }

    public Result<Guid> PlaceLegalHold(string reasonCode, Guid placedBy, DateTimeOffset now)
    {
        if (Status == DocumentStatus.Disposed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The document was disposed of.");
        }

        var hold = Documents.LegalHold.Place(Id, reasonCode, placedBy, now);
        _holds.Add(hold);
        LegalHold = true;
        MarkChanged();
        return hold.Id;
    }

    public Result ReleaseLegalHold(Guid holdId, DateTimeOffset now)
    {
        var hold = _holds.SingleOrDefault(h => h.Id == holdId && h.IsActive);
        if (hold is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The document has no such active legal hold.");
        }

        hold.Release(now);
        LegalHold = _holds.Any(h => h.IsActive);
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command DisposeDocument (transition "Retention ended, no hold"). The caller deletes the content from storage.</summary>
    public Result DisposeDocument(DateOnly today, DateTimeOffset now)
    {
        if (LegalHold)
        {
            return Error.Conflict(DocumentErrorCodes.OnLegalHold, "The document is on legal hold.");
        }

        if (RetentionUntil is null)
        {
            return Error.Conflict(DocumentErrorCodes.RetentionNotEnded, "The document has no retention date yet, so it is kept.");
        }

        if (RetentionUntil >= today)
        {
            return Error.Conflict(DocumentErrorCodes.RetentionNotEnded, $"The document is kept until {RetentionUntil:yyyy-MM-dd}.");
        }

        var result = Fire(DocumentTrigger.RetentionEndedNoHold);
        if (result.IsSuccess)
        {
            DisposedAt = now;
            Raise(new DocumentDisposed(Id, now));
        }

        return result;
    }

    private Result Fire(DocumentTrigger trigger)
    {
        var next = Lifecycle.Fire(Status, trigger);
        if (next.IsFailure)
        {
            return next.Error;
        }

        Status = next.Value;
        MarkChanged();
        return Result.Success();
    }
}

public static class DocumentErrorCodes
{
    [ErrorCode(409, "error.document.on_legal_hold")]
    public const string OnLegalHold = "DOCUMENT_ON_LEGAL_HOLD";

    [ErrorCode(409, "error.document.retention_not_ended")]
    public const string RetentionNotEnded = "DOCUMENT_RETENTION_NOT_ENDED";

    [ErrorCode(422, "error.document.merge_field_missing")]
    public const string MergeFieldMissing = "DOCUMENT_MERGE_FIELD_MISSING";

    [ErrorCode(422, "error.document.code_not_in_force")]
    public const string CodeNotInForce = "DOCUMENT_CODE_NOT_IN_FORCE";

    [ErrorCode(403, "error.document.role_required")]
    public const string RoleRequired = "DOCUMENT_ROLE_REQUIRED";

    [ErrorCode(422, "error.document.purpose_required")]
    public const string PurposeRequired = "DOCUMENT_PURPOSE_REQUIRED";

    [ErrorCode(409, "error.document.content_mismatch")]
    public const string ContentMismatch = "DOCUMENT_CONTENT_MISMATCH";
}
