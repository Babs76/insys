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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Documents.Application;
using Insys.Documents.Domain.Documents;
using Insys.Documents.Infrastructure.StaticData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime.Text;

namespace Insys.Documents.Infrastructure.Persistence;

internal sealed class DocumentRepository(DocumentsDbContext db) : IDocumentRepository
{
    public Task<Document?> Get(Guid id, CancellationToken cancellationToken) =>
        db.Documents.Include(d => d.Deliveries).Include(d => d.Holds).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Document>> ForBusinessRef(string businessRefType, Guid businessRefId, CancellationToken cancellationToken) =>
        await db.Documents.AsNoTracking().Include(d => d.Deliveries).Include(d => d.Holds)
            .Where(d => d.BusinessRefType == businessRefType && d.BusinessRefId == businessRefId).OrderBy(d => d.Id).ToListAsync(cancellationToken);

    public void Add(Document document) => db.Documents.Add(document);

    public void Add(GenerationRequest request) => db.Set<GenerationRequest>().Add(request);

    public void Add(AccessRecord record) => db.Set<AccessRecord>().Add(record);
}

internal sealed class DocumentCatalogue(DocumentsDbContext db) : IDocumentCatalogue
{
    public async Task<(string Classification, IReadOnlyList<string> AllowedRoles)?> Category(string categoryCode, DateOnly day, CancellationToken cancellationToken)
    {
        var category = await InForce<DocumentCategories>(day).Where(c => c.CategoryCode == categoryCode).OrderByDescending(c => c.TenantId != null)
            .FirstOrDefaultAsync(cancellationToken);
        return category is null ? null : (category.Classification, category.AllowedRoles);
    }

    public async Task<TemplateDefinition?> Template(string templateCode, string language, DateOnly day, CancellationToken cancellationToken)
    {
        var template = await InForce<DocumentTemplates>(day).Where(t => t.TemplateCode == templateCode && t.Language == language).OrderByDescending(t => t.TenantId != null)
            .FirstOrDefaultAsync(cancellationToken);
        return template is null ? null : new TemplateDefinition(template.TemplateCode, template.Version, template.Language, template.DocumentType, template.BodyRef, template.MergeFields);
    }

    public async Task<DateOnly?> RetentionUntil(string categoryCode, string? jurisdiction, DateOnly created, CancellationToken cancellationToken)
    {
        var schedule = await InForce<RetentionSchedules>(created).Where(s => s.Category == categoryCode && s.Jurisdiction == jurisdiction)
            .OrderByDescending(s => s.TenantId != null).FirstOrDefaultAsync(cancellationToken);

        // TODO(spec): retention from policy end or claim closure starts when that event arrives; until then there is no date (OQ-056).
        if (schedule is null || schedule.TriggerEvent != "CREATION")
        {
            return null;
        }

        var period = PeriodPattern.NormalizingIso.Parse(schedule.Period);
        return period.Success ? NodaTime.LocalDate.FromDateOnly(created).Plus(period.Value).ToDateOnly() : null;
    }

    public Task<bool> LegalHoldReasonExists(string reasonCode, DateOnly day, CancellationToken cancellationToken) =>
        InForce<LegalHoldReasonCodes>(day).AnyAsync(r => r.Code == reasonCode, cancellationToken);

    public Task<bool> DeliveryChannelExists(string channel, DateOnly day, CancellationToken cancellationToken) =>
        InForce<DeliveryChannelSettings>(day).AnyAsync(c => c.Channel == channel, cancellationToken);

    private IQueryable<T> InForce<T>(DateOnly day) where T : StaticRecord =>
        db.Set<T>().AsNoTracking().InForce(DocumentsStaticData.Descriptors.Single(d => d.EntityType == typeof(T)).Lifecycle, day);
}

public sealed class DocumentStorageOptions
{
    /// <summary>The folder the development storage writes to. TODO(spec): the production document store is not specified (OQ-056).</summary>
    public string Root { get; set; } = Path.Combine(Path.GetTempPath(), "insys-documents");
}

/// <summary>
/// Development storage on the local file system, one folder per tenant (DOC-001). A location outside the calling
/// tenant's folder is refused. Replace with the production object store.
/// </summary>
internal sealed class FileSystemDocumentStorage(IOptions<DocumentStorageOptions> options) : IDocumentStorage
{
    public async Task<StoredContent> Put(Guid tenantId, Guid documentId, byte[] content, string mimeType, CancellationToken cancellationToken)
    {
        var uri = $"tenant/{tenantId:N}/documents/{documentId:N}";
        var path = PathFor(tenantId, uri);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        return new StoredContent(uri, mimeType, content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    public async Task<byte[]?> Get(Guid tenantId, string storageUri, CancellationToken cancellationToken)
    {
        var path = PathFor(tenantId, storageUri);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public Task Delete(Guid tenantId, string storageUri, CancellationToken cancellationToken)
    {
        File.Delete(PathFor(tenantId, storageUri));
        return Task.CompletedTask;
    }

    private string PathFor(Guid tenantId, string storageUri)
    {
        var prefix = $"tenant/{tenantId:N}/";
        if (!storageUri.StartsWith(prefix, StringComparison.Ordinal) || storageUri.Contains("..", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The document belongs to another tenant's storage.");
        }

        return Path.Combine(options.Value.Root, storageUri.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Stand-in renderer: a plain-text document listing the template's merge fields with their values. Every merge field
/// must be supplied. TODO(spec): rendering the template body (body_ref) needs the production template engine (OQ-056).
/// </summary>
internal sealed class PlainTextDocumentRenderer : IDocumentRenderer
{
    public Result<(byte[] Content, string MimeType)> Render(TemplateDefinition template, IReadOnlyDictionary<string, JsonElement> data)
    {
        var missing = template.MergeFields.Where(f => !data.ContainsKey(f)).ToList();
        if (missing.Count > 0)
        {
            return Error.Validation(DocumentErrorCodes.MergeFieldMissing, $"Template {template.TemplateCode} needs {string.Join(", ", missing)}.");
        }

        var text = new StringBuilder();
        text.Append(template.DocumentType).Append(" (").Append(template.TemplateCode).Append(" v").Append(template.Version).Append(", ").Append(template.Language).AppendLine(")");
        foreach (var field in template.MergeFields)
        {
            var value = data[field];
            text.Append(field).Append(": ").AppendLine(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        }

        return (Encoding.UTF8.GetBytes(text.ToString()), "text/plain");
    }
}

/// <summary>Stand-in delivery: records a provider reference without sending. TODO(spec): delivery providers are configured per channel (OQ-056).</summary>
internal sealed class StubDocumentDelivery(ILogger<StubDocumentDelivery> logger) : IDocumentDelivery
{
    public Task<DeliveryReceipt> Send(string channel, string recipient, Guid documentId, string mimeType, byte[] content, CancellationToken cancellationToken)
    {
        logger.LogInformation("Stub delivery of document {DocumentId} by {Channel}", documentId, channel);
        return Task.FromResult(new DeliveryReceipt(true, "STUB-" + Guid.CreateVersion7().ToString("N")[..16].ToUpperInvariant()));
    }
}

internal sealed class DocumentsEventMapper : IDomainEventMapper
{
    public IIntegrationEvent? Map(IDomainEvent domainEvent) => domainEvent switch
    {
        DocumentGenerated e => new Contracts.DocumentGenerated(e.DocumentId, e.CategoryCode, e.BusinessRefType, e.BusinessRefId, e.TemplateCode, e.TemplateVersion),
        DocumentDelivered e => new Contracts.DocumentDelivered(e.DocumentId, e.Channel),
        DocumentDisposed e => new Contracts.DocumentDisposed(e.DocumentId),
        _ => null,
    };
}
