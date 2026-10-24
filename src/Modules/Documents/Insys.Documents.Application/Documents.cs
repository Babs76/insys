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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Documents.Domain.Documents;
using Insys.Identity.Contracts;

namespace Insys.Documents.Application;

/// <summary>Tenant-scoped document storage (DOC-001). A location from another tenant is never read.</summary>
public interface IDocumentStorage
{
    Task<StoredContent> Put(Guid tenantId, Guid documentId, byte[] content, string mimeType, CancellationToken cancellationToken);

    Task<byte[]?> Get(Guid tenantId, string storageUri, CancellationToken cancellationToken);

    Task Delete(Guid tenantId, string storageUri, CancellationToken cancellationToken);
}

/// <summary>A template version in force (document.cfg_document_templates).</summary>
public sealed record TemplateDefinition(string TemplateCode, int Version, string Language, string DocumentType, string BodyRef, IReadOnlyList<string> MergeFields);

public interface IDocumentRenderer
{
    Result<(byte[] Content, string MimeType)> Render(TemplateDefinition template, IReadOnlyDictionary<string, JsonElement> data);
}

public sealed record DeliveryReceipt(bool Sent, string? ProviderRef);

public interface IDocumentDelivery
{
    Task<DeliveryReceipt> Send(string channel, string recipient, Guid documentId, string mimeType, byte[] content, CancellationToken cancellationToken);
}

/// <summary>The Documents module's static data in force.</summary>
public interface IDocumentCatalogue
{
    Task<(string Classification, IReadOnlyList<string> AllowedRoles)?> Category(string categoryCode, DateOnly day, CancellationToken cancellationToken);

    Task<TemplateDefinition?> Template(string templateCode, string language, DateOnly day, CancellationToken cancellationToken);

    /// <summary>The retention date from the schedule for the category and jurisdiction, when its trigger is creation; otherwise null.</summary>
    Task<DateOnly?> RetentionUntil(string categoryCode, string? jurisdiction, DateOnly created, CancellationToken cancellationToken);

    Task<bool> LegalHoldReasonExists(string reasonCode, DateOnly day, CancellationToken cancellationToken);

    Task<bool> DeliveryChannelExists(string channel, DateOnly day, CancellationToken cancellationToken);
}

public interface IDocumentRepository
{
    Task<Document?> Get(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Document>> ForBusinessRef(string businessRefType, Guid businessRefId, CancellationToken cancellationToken);

    void Add(Document document);

    void Add(GenerationRequest request);

    void Add(AccessRecord record);
}

public sealed record DeliveryView(Guid Id, string Channel, string Recipient, [property: OneOf("SENT", "FAILED")] string Status, string? ProviderRef, DateTimeOffset? SentAt);

public sealed record LegalHoldView(Guid Id, string ReasonCode, Guid PlacedBy, DateTimeOffset PlacedAt, DateTimeOffset? ReleasedAt);

public sealed record DocumentView(Guid Id, string CategoryCode, string Classification, DocumentStatus Status, string MimeType, long SizeBytes, string Sha256, string? TemplateCode,
    int? TemplateVersion, string? ProductVersionRef, string BusinessRefType, Guid BusinessRefId, DateOnly? RetentionUntil, bool LegalHold, DateTimeOffset? DisposedAt,
    IReadOnlyList<DeliveryView> Deliveries, IReadOnlyList<LegalHoldView> Holds, long RowVersion)
{
    public static DocumentView From(Document d) => new(d.Id, d.CategoryCode, d.Classification, d.Status, d.MimeType, d.SizeBytes, d.Sha256, d.TemplateCode,
        d.TemplateVersion, d.ProductVersionRef, d.BusinessRefType, d.BusinessRefId, d.RetentionUntil, d.LegalHold, d.DisposedAt,
        d.Deliveries.OrderBy(x => x.Id).Select(x => new DeliveryView(x.Id, x.Channel, x.Recipient, x.Status, x.ProviderRef, x.SentAt)).ToList(),
        d.Holds.OrderBy(h => h.Id).Select(h => new LegalHoldView(h.Id, h.ReasonCode, h.PlacedBy, h.PlacedAt, h.ReleasedAt)).ToList(), d.RowVersion);
}

public sealed record DocumentContent(byte[] Content, string MimeType);

/// <summary>Command GenerateDocument: renders the template version in force with the data, stores it, and records the template and product versions (DOC-002).</summary>
public sealed record GenerateDocument(string TemplateCode, string Language, string CategoryCode, string BusinessRefType, Guid BusinessRefId, string? ProductVersionRef,
    string? Jurisdiction, JsonElement Data) : ICommand<DocumentView>;

/// <summary>Command StoreDocument: an uploaded document.</summary>
public sealed record StoreDocument(string CategoryCode, string BusinessRefType, Guid BusinessRefId, string MimeType, byte[] Content, string? ProductVersionRef,
    string? Jurisdiction) : ICommand<DocumentView>;

public sealed record DeliverDocument(Guid DocumentId, string Channel, string Recipient) : ICommand<DeliveryView>;

public sealed record PlaceLegalHold(Guid DocumentId, string ReasonCode) : ICommand<Guid>;

public sealed record ReleaseLegalHold(Guid DocumentId, Guid HoldId) : ICommand<long>;

public sealed record DisposeDocument(Guid DocumentId, long ExpectedVersion) : ICommand<long>, IVersionedCommand;

/// <summary>Reads a document's content, recording the access for sensitive documents (DOC-003) and checking the allowed roles for highly sensitive ones (DOC-004).</summary>
public sealed record ReadDocumentContent(Guid DocumentId, string? Purpose) : ICommand<DocumentContent>;

public sealed record GetDocument(Guid DocumentId) : IQuery<DocumentView>;

public sealed record ListDocuments(string BusinessRefType, Guid BusinessRefId) : IQuery<IReadOnlyList<DocumentView>>;

/// <summary>
/// The business reference types INSYS files the documents it generates under (GAP-033): a General policy and endorsement,
/// a Life policy, a Health remittance. A stored or generated document may carry another type, so this is not a closed list.
/// </summary>
public static class DocumentReferenceTypes
{
    public static readonly IReadOnlyList<string> Known =
    [
        General.Contracts.GeneralDocuments.PolicyRefType, General.Contracts.GeneralDocuments.EndorsementRefType, Life.Contracts.LifeDocuments.PolicyRefType,
        Health.Contracts.HealthReferences.RemittanceRefType,
    ];
}

public sealed class GenerateDocumentValidator : AbstractValidator<GenerateDocument>
{
    public GenerateDocumentValidator()
    {
        RuleFor(c => c.TemplateCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.Language).Length(2);
        RuleFor(c => c.CategoryCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BusinessRefType).NotEmpty().MaximumLength(30);
        RuleFor(c => c.BusinessRefId).NotEmpty();
        RuleFor(c => c.ProductVersionRef).MaximumLength(30);
        RuleFor(c => c.Data.ValueKind).Equal(JsonValueKind.Object).WithMessage("Data must be a JSON object of merge field values.");
    }
}

public sealed class StoreDocumentValidator : AbstractValidator<StoreDocument>
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public StoreDocumentValidator()
    {
        RuleFor(c => c.CategoryCode).NotEmpty().MaximumLength(20);
        RuleFor(c => c.BusinessRefType).NotEmpty().MaximumLength(30);
        RuleFor(c => c.BusinessRefId).NotEmpty();
        RuleFor(c => c.MimeType).NotEmpty().MaximumLength(80);
        RuleFor(c => c.Content).NotEmpty().Must(c => c.Length <= MaxBytes).WithMessage("A stored document is at most 10 MB.");
        RuleFor(c => c.ProductVersionRef).MaximumLength(30);
    }
}

public sealed class DeliverDocumentValidator : AbstractValidator<DeliverDocument>
{
    public DeliverDocumentValidator()
    {
        RuleFor(c => c.Channel).NotEmpty().MaximumLength(10);
        RuleFor(c => c.Recipient).NotEmpty().MaximumLength(200);
    }
}

internal static class Users
{
    public static Result<Guid> Require(IExecutionContext execution) =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out var id)
            ? id
            : Error.Forbidden(ErrorCodes.Forbidden, "This is done by an identified user.");
}

internal static class Loaded
{
    public static async Task<Result<Document>> Document(IDocumentRepository documents, Guid id, CancellationToken cancellationToken) =>
        await documents.Get(id, cancellationToken) is { } document ? document : Error.NotFound(ErrorCodes.NotFound, "The document was not found.");
}

internal sealed class GenerateDocumentHandler(DocumentGenerator generator) : ICommandHandler<GenerateDocument, DocumentView>
{
    public async Task<Result<DocumentView>> Handle(GenerateDocument command, CancellationToken cancellationToken)
    {
        var document = await generator.Generate(command, cancellationToken);
        return document.IsFailure ? document.Error : DocumentView.From(document.Value);
    }
}

/// <summary>Renders the template version in force with the data, stores it, and records the template and product versions (DOC-002).</summary>
public sealed class DocumentGenerator(IDocumentRepository documents, IDocumentCatalogue catalogue, IDocumentRenderer renderer, IDocumentStorage storage,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock)
{
    public async Task<Result<Document>> Generate(GenerateDocument command, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        var category = await catalogue.Category(command.CategoryCode, today, cancellationToken);
        var template = await catalogue.Template(command.TemplateCode, command.Language, today, cancellationToken);
        if (category is null || template is null)
        {
            return Error.Validation(DocumentErrorCodes.CodeNotInForce,
                category is null ? $"{command.CategoryCode} is not a document category in force." : $"No {command.Language} version of template {command.TemplateCode} is in force.");
        }

        var data = command.Data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var rendered = renderer.Render(template, data);
        if (rendered.IsFailure)
        {
            return rendered.Error;
        }

        var id = Guid.CreateVersion7();
        var stored = await storage.Put(tenant.RequireTenantId(), id, rendered.Value.Content, rendered.Value.MimeType, cancellationToken);
        var retention = await catalogue.RetentionUntil(command.CategoryCode, command.Jurisdiction, today, cancellationToken);
        var document = Document.Create(id, command.CategoryCode, category.Value.Classification, stored, new TemplateVersionRef(template.TemplateCode, template.Version),
            command.ProductVersionRef, command.BusinessRefType, command.BusinessRefId, retention, clock.UtcNow);
        documents.Add(document);
        documents.Add(GenerationRequest.Generated(new TemplateVersionRef(template.TemplateCode, template.Version), command.Language,
            JsonSerializer.Serialize(new { businessRefType = command.BusinessRefType, businessRefId = command.BusinessRefId, fields = data.Keys.Order(StringComparer.Ordinal) },
                JsonSerializerOptions.Web), id));
        return document;
    }
}

internal sealed class StoreDocumentHandler(IDocumentRepository documents, IDocumentCatalogue catalogue, IDocumentStorage storage, ITenantContext tenant, IBusinessDate businessDate, IClock clock)
    : ICommandHandler<StoreDocument, DocumentView>
{
    public async Task<Result<DocumentView>> Handle(StoreDocument command, CancellationToken cancellationToken)
    {
        var today = businessDate.Today;
        if (await catalogue.Category(command.CategoryCode, today, cancellationToken) is not { } category)
        {
            return Error.Validation(DocumentErrorCodes.CodeNotInForce, $"{command.CategoryCode} is not a document category in force.");
        }

        var id = Guid.CreateVersion7();
        var stored = await storage.Put(tenant.RequireTenantId(), id, command.Content, command.MimeType, cancellationToken);
        var document = Document.Create(id, command.CategoryCode, category.Classification, stored, null, command.ProductVersionRef, command.BusinessRefType,
            command.BusinessRefId, await catalogue.RetentionUntil(command.CategoryCode, command.Jurisdiction, today, cancellationToken), clock.UtcNow);
        documents.Add(document);
        return DocumentView.From(document);
    }
}

internal sealed class DeliverDocumentHandler(IDocumentRepository documents, IDocumentCatalogue catalogue, IDocumentStorage storage, IDocumentDelivery delivery,
    ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<DeliverDocument, DeliveryView>
{
    public async Task<Result<DeliveryView>> Handle(DeliverDocument command, CancellationToken cancellationToken)
    {
        var document = await Loaded.Document(documents, command.DocumentId, cancellationToken);
        if (document.IsFailure)
        {
            return document.Error;
        }

        if (!await catalogue.DeliveryChannelExists(command.Channel, businessDate.Today, cancellationToken))
        {
            return Error.Validation(DocumentErrorCodes.CodeNotInForce, $"No delivery channel {command.Channel} is configured.");
        }

        var content = document.Value.Status == DocumentStatus.Disposed ? null : await storage.Get(tenant.RequireTenantId(), document.Value.StorageUri, cancellationToken);
        var receipt = content is null
            ? new DeliveryReceipt(false, null)
            : await delivery.Send(command.Channel, command.Recipient, document.Value.Id, document.Value.MimeType, content, cancellationToken);
        audit.Record(new AuditEntry("DOCUMENT", "DeliverDocument", nameof(Document), document.Value.Id.ToString(),
            new Dictionary<string, string?> { ["channel"] = command.Channel, ["sent"] = receipt.Sent.ToString() }));
        var result = document.Value.DeliverDocument(command.Channel, command.Recipient, receipt.Sent, receipt.ProviderRef, clock.UtcNow);
        return result.IsFailure
            ? result.Error
            : new DeliveryView(result.Value.Id, result.Value.Channel, result.Value.Recipient, result.Value.Status, result.Value.ProviderRef, result.Value.SentAt);
    }
}

internal sealed class PlaceLegalHoldHandler(IDocumentRepository documents, IDocumentCatalogue catalogue, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<PlaceLegalHold, Guid>
{
    public async Task<Result<Guid>> Handle(PlaceLegalHold command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var document = await Loaded.Document(documents, command.DocumentId, cancellationToken);
        if (user.IsFailure || document.IsFailure)
        {
            return user.IsFailure ? user.Error : document.Error!;
        }

        if (!await catalogue.LegalHoldReasonExists(command.ReasonCode, businessDate.Today, cancellationToken))
        {
            return Error.Validation(DocumentErrorCodes.CodeNotInForce, $"{command.ReasonCode} is not a legal hold reason in force.");
        }

        audit.Record(new AuditEntry("DOCUMENT", "PlaceLegalHold", nameof(Document), command.DocumentId.ToString(),
            new Dictionary<string, string?> { ["reason"] = command.ReasonCode }));
        return document.Value.PlaceLegalHold(command.ReasonCode, user.Value, clock.UtcNow);
    }
}

internal sealed class ReleaseLegalHoldHandler(IDocumentRepository documents, IClock clock, IAuditTrail audit) : ICommandHandler<ReleaseLegalHold, long>
{
    public async Task<Result<long>> Handle(ReleaseLegalHold command, CancellationToken cancellationToken)
    {
        var document = await Loaded.Document(documents, command.DocumentId, cancellationToken);
        if (document.IsFailure)
        {
            return document.Error;
        }

        audit.Record(new AuditEntry("DOCUMENT", "ReleaseLegalHold", nameof(Document), command.DocumentId.ToString()));
        var result = document.Value.ReleaseLegalHold(command.HoldId, clock.UtcNow);
        return result.IsFailure ? result.Error : document.Value.RowVersion + 1;
    }
}

internal sealed class DisposeDocumentHandler(IDocumentRepository documents, IDocumentStorage storage, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit)
    : ICommandHandler<DisposeDocument, long>
{
    public async Task<Result<long>> Handle(DisposeDocument command, CancellationToken cancellationToken)
    {
        var document = await Loaded.Document(documents, command.DocumentId, cancellationToken);
        if (document.IsFailure)
        {
            return document.Error;
        }

        if (document.Value.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The document has changed since it was read.");
        }

        audit.Record(new AuditEntry("DOCUMENT", "DisposeDocument", nameof(Document), command.DocumentId.ToString()));
        var result = document.Value.DisposeDocument(businessDate.Today, clock.UtcNow);
        if (result.IsFailure)
        {
            return result.Error;
        }

        // TODO(spec): disposal methods ANONYMISE and ARCHIVE are not specified beyond their names; every disposal deletes the content (OQ-056).
        await storage.Delete(tenant.RequireTenantId(), document.Value.StorageUri, cancellationToken);
        return document.Value.RowVersion + 1;
    }
}

internal sealed class ReadDocumentContentHandler(IDocumentRepository documents, IDocumentCatalogue catalogue, IDocumentStorage storage, IUserRoleQuery roles,
    IExecutionContext execution, ITenantContext tenant, IBusinessDate businessDate, IClock clock, IAuditTrail audit) : ICommandHandler<ReadDocumentContent, DocumentContent>
{
    public async Task<Result<DocumentContent>> Handle(ReadDocumentContent command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        var document = await Loaded.Document(documents, command.DocumentId, cancellationToken);
        if (user.IsFailure || document.IsFailure)
        {
            return user.IsFailure ? user.Error : document.Error!;
        }

        var doc = document.Value;
        if (doc.Status == DocumentStatus.Disposed)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The document was disposed of.");
        }

        if (Classifications.IsSensitive(doc.Classification))
        {
            if (string.IsNullOrWhiteSpace(command.Purpose))
            {
                return Error.Validation(DocumentErrorCodes.PurposeRequired, "Reading a sensitive document needs a purpose, which is recorded (DOC-003).");
            }

            if (Classifications.NeedsAllowedRole(doc.Classification))
            {
                var category = await catalogue.Category(doc.CategoryCode, businessDate.Today, cancellationToken);
                var held = await roles.EffectiveRoles(tenant.RequireTenantId(), user.Value, cancellationToken);
                if (category is null || !held.Intersect(category.Value.AllowedRoles, StringComparer.Ordinal).Any())
                {
                    audit.Record(new AuditEntry("DOCUMENT", "ReadDocumentDenied", nameof(Document), doc.Id.ToString()));
                    return Error.Forbidden(DocumentErrorCodes.RoleRequired, $"Documents of category {doc.CategoryCode} are read only by its allowed roles (DOC-004).");
                }
            }

            documents.Add(AccessRecord.Create(doc.Id, user.Value, "READ", command.Purpose, clock.UtcNow));
            audit.Record(new AuditEntry("DOCUMENT", "ReadDocument", nameof(Document), doc.Id.ToString(),
                new Dictionary<string, string?> { ["classification"] = doc.Classification, ["purpose"] = command.Purpose }));
        }

        var content = await storage.Get(tenant.RequireTenantId(), doc.StorageUri, cancellationToken);
        if (content is null || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)) != doc.Sha256)
        {
            return Error.Conflict(DocumentErrorCodes.ContentMismatch, "The stored content is missing or does not match its checksum.");
        }

        return new DocumentContent(content, doc.MimeType);
    }
}

internal sealed class GetDocumentHandler(IDocumentRepository documents) : IQueryHandler<GetDocument, DocumentView>
{
    public async Task<Result<DocumentView>> Handle(GetDocument query, CancellationToken cancellationToken) =>
        await documents.Get(query.DocumentId, cancellationToken) is { } document ? DocumentView.From(document) : Error.NotFound(ErrorCodes.NotFound, "The document was not found.");
}

internal sealed class ListDocumentsHandler(IDocumentRepository documents) : IQueryHandler<ListDocuments, IReadOnlyList<DocumentView>>
{
    public async Task<Result<IReadOnlyList<DocumentView>>> Handle(ListDocuments query, CancellationToken cancellationToken) =>
        (await documents.ForBusinessRef(query.BusinessRefType, query.BusinessRefId, cancellationToken)).Select(DocumentView.From).ToList();
}
