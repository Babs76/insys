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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Domain.Documents;
using Insys.Identity.Contracts;

namespace Insys.Documents.Application;

// External-user access (docs/specs/addenda/external-user-access.md §2): documents in the portals. A portal user reads a
// document when (1) its business record is in their relationship, as the module owning the record answers, and (2) its
// category's allowed roles include one of theirs, whatever its classification. So each tenant chooses which categories
// its portals see by naming the portal roles on them; none is shown by default. Every portal read is logged (DOC-003).

public sealed record PortalDocumentView(Guid Id, string CategoryCode, string MimeType, long SizeBytes, string BusinessRefType, Guid BusinessRefId)
{
    public static PortalDocumentView From(Document d) => new(d.Id, d.CategoryCode, d.MimeType, d.SizeBytes, d.BusinessRefType, d.BusinessRefId);
}

public sealed record ListPortalDocuments(string BusinessRefType, Guid BusinessRefId) : IQuery<IReadOnlyList<PortalDocumentView>>;

/// <summary>Command ReadPortalDocumentContent: the content, recorded in the document's access log with purpose PORTAL.</summary>
public sealed record ReadPortalDocumentContent(Guid DocumentId) : ICommand<DocumentContent>;

public sealed class PortalDocuments(IEnumerable<IPortalRecordRelations> relations, IDocumentCatalogue catalogue, IUserRoleQuery roles, ITenantContext tenant,
    IExecutionContext execution, IBusinessDate businessDate)
{
    public const string Purpose = "PORTAL";

    public ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    public Guid UserId => Guid.Parse(execution.ActorId!);

    public async Task<bool> RecordRelated(string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        relations.FirstOrDefault(r => r.Covers(businessRefType)) is { } owner
        && await owner.Related(tenant.RequireTenantId(), Who, businessRefType, businessRefId, cancellationToken);

    /// <summary>The categories, among those given, whose allowed roles include one of the caller's.</summary>
    public async Task<IReadOnlySet<string>> Readable(IEnumerable<string> categoryCodes, CancellationToken cancellationToken)
    {
        var held = await roles.EffectiveRoles(tenant.RequireTenantId(), UserId, cancellationToken);
        var readable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in categoryCodes.Distinct(StringComparer.Ordinal))
        {
            if (await catalogue.Category(code, businessDate.Today, cancellationToken) is { } category && held.Intersect(category.AllowedRoles, StringComparer.Ordinal).Any())
            {
                readable.Add(code);
            }
        }

        return readable;
    }
}

internal sealed class ListPortalDocumentsHandler(IDocumentRepository documents, PortalDocuments portal) : IQueryHandler<ListPortalDocuments, IReadOnlyList<PortalDocumentView>>
{
    public async Task<Result<IReadOnlyList<PortalDocumentView>>> Handle(ListPortalDocuments query, CancellationToken cancellationToken)
    {
        if (!await portal.RecordRelated(query.BusinessRefType, query.BusinessRefId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The record was not found.");
        }

        var filed = (await documents.ForBusinessRef(query.BusinessRefType, query.BusinessRefId, cancellationToken)).Where(d => d.Status != DocumentStatus.Disposed).ToList();
        var readable = await portal.Readable(filed.Select(d => d.CategoryCode), cancellationToken);
        return filed.Where(d => readable.Contains(d.CategoryCode)).Select(PortalDocumentView.From).ToList();
    }
}

internal sealed class ReadPortalDocumentContentHandler(IDocumentRepository documents, IDocumentStorage storage, PortalDocuments portal, ITenantContext tenant, IClock clock,
    IAuditTrail audit) : ICommandHandler<ReadPortalDocumentContent, DocumentContent>
{
    public async Task<Result<DocumentContent>> Handle(ReadPortalDocumentContent command, CancellationToken cancellationToken)
    {
        // Not found alike for a document outside the relationship, of a category the portal may not read, or disposed of.
        if (await documents.Get(command.DocumentId, cancellationToken) is not { } doc || doc.Status == DocumentStatus.Disposed
            || !await portal.RecordRelated(doc.BusinessRefType, doc.BusinessRefId, cancellationToken)
            || !(await portal.Readable([doc.CategoryCode], cancellationToken)).Contains(doc.CategoryCode))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The document was not found.");
        }

        var content = await storage.Get(tenant.RequireTenantId(), doc.StorageUri, cancellationToken);
        if (content is null || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)) != doc.Sha256)
        {
            return Error.Conflict(DocumentErrorCodes.ContentMismatch, "The stored content is missing or does not match its checksum.");
        }

        documents.Add(AccessRecord.Create(doc.Id, portal.UserId, "READ", PortalDocuments.Purpose, clock.UtcNow));
        audit.Record(new AuditEntry("DOCUMENT", "ReadDocument", nameof(Document), doc.Id.ToString(),
            new Dictionary<string, string?> { ["classification"] = doc.Classification, ["purpose"] = PortalDocuments.Purpose }));
        return new DocumentContent(content, doc.MimeType);
    }
}
