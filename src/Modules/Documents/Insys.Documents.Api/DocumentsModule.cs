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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Documents.Api.StaticData;
using Insys.Documents.Application;
using Insys.Documents.Contracts;
using Insys.Documents.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Documents.Api;

/// <param name="Data">The merge field values, by field key (for example "policy.number").</param>
public sealed record GenerateDocumentRequest(string TemplateCode, string Language, string CategoryCode, string BusinessRefType, Guid BusinessRefId,
    string? ProductVersionRef, string? Jurisdiction, JsonElement Data);

/// <param name="ContentBase64">The file, base64-encoded; at most 10 MB.</param>
public sealed record StoreDocumentRequest(string CategoryCode, string BusinessRefType, Guid BusinessRefId, string MimeType, string ContentBase64,
    string? ProductVersionRef, string? Jurisdiction);

public sealed record DeliverDocumentRequest(string Channel, string Recipient);

public sealed record LegalHoldRequest(string ReasonCode);

/// <summary>The Documents module: generation, storage, delivery, retention and audited access (DOC-001 to DOC-004).</summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "Documents";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddDocumentsInfrastructure(configuration);

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        DocumentsStaticDataEndpoints.MapStaticData(api);
        var documents = api.MapGroup("/documents/documents").WithTags(Name);

        documents.MapPost("/generate", async (GenerateDocumentRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new GenerateDocument(r.TemplateCode, r.Language, r.CategoryCode, r.BusinessRefType, r.BusinessRefId, r.ProductVersionRef,
                    r.Jurisdiction, r.Data), ct)).ToHttp(view => Created(http, view)))
            .Produces<DocumentView>(StatusCodes.Status201Created)
            .WithName("DocumentsGenerate")
            .WithSummary("Generate a document from the template version in force")
            .WithDescription("Records the template version and product version it was generated from (DOC-002). Every merge field of the template must be supplied.")
            .RequirePermission(DocumentPermissions.DocumentCreate)
            .RequireIdempotencyKey();

        documents.MapPost("/", async (StoreDocumentRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(r.ContentBase64);
                }
                catch (FormatException)
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "contentBase64 is not base64.", StatusCodes.Status422UnprocessableEntity);
                }

                return (await dispatcher.Send(new StoreDocument(r.CategoryCode, r.BusinessRefType, r.BusinessRefId, r.MimeType, content, r.ProductVersionRef,
                    r.Jurisdiction), ct)).ToHttp(view => Created(http, view));
            })
            .Produces<DocumentView>(StatusCodes.Status201Created)
            .WithName("DocumentsStore")
            .WithSummary("Store an uploaded document in tenant-scoped storage (DOC-001)")
            .RequirePermission(DocumentPermissions.DocumentCreate)
            .RequireIdempotencyKey();

        documents.MapGet("/", async (string businessRefType, Guid businessRefId, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListDocuments(businessRefType, businessRefId), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<DocumentView>>(StatusCodes.Status200OK)
            .WithName("DocumentsList")
            .WithSummary("List the documents of a business reference")
            .RequirePermission(DocumentPermissions.DocumentRead);

        documents.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetDocument(id), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<DocumentView>(StatusCodes.Status200OK)
            .WithName("DocumentsGet")
            .WithSummary("Get a document's record (not its content)")
            .RequirePermission(DocumentPermissions.DocumentRead);

        documents.MapGet("/{id:guid}/content", async (Guid id, string? purpose, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReadDocumentContent(id, purpose), ct)).ToHttp(content => Results.File(content.Content, content.MimeType)))
            .Produces(StatusCodes.Status200OK, typeof(Stream), "application/octet-stream")
            .WithName("DocumentsReadContent")
            .WithSummary("Read a document's content")
            .WithDescription("Reading a confidential, personal or highly sensitive document needs a purpose and is recorded (DOC-003); a highly sensitive "
                + "one also needs one of its category's allowed roles (DOC-004).")
            .RequirePermission(DocumentPermissions.DocumentRead);

        documents.MapPost("/{id:guid}/deliveries", async (Guid id, DeliverDocumentRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new DeliverDocument(id, r.Channel, r.Recipient), ct)).ToHttp(Results.Ok))
            .Produces<DeliveryView>(StatusCodes.Status200OK)
            .WithName("DocumentsDeliver")
            .WithSummary("Deliver a document by a configured channel; every attempt is recorded")
            .RequirePermission(DocumentPermissions.DocumentDeliver)
            .RequireIdempotencyKey();

        documents.MapPost("/{id:guid}/legal-holds", async (Guid id, LegalHoldRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new PlaceLegalHold(id, r.ReasonCode), ct)).ToHttp(holdId => Results.Ok(new CreatedId(holdId))))
            .Produces<CreatedId>(StatusCodes.Status200OK)
            .WithName("DocumentsPlaceLegalHold")
            .WithSummary("Place a legal hold; the document cannot be disposed of while it holds")
            .RequirePermission(DocumentPermissions.LegalHoldManage);

        documents.MapPost("/{id:guid}/legal-holds/{holdId:guid}/release", async (Guid id, Guid holdId, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReleaseLegalHold(id, holdId), ct)).ToHttp(_ => Results.NoContent()))
            .Produces(StatusCodes.Status204NoContent)
            .WithName("DocumentsReleaseLegalHold")
            .WithSummary("Release a legal hold")
            .RequirePermission(DocumentPermissions.LegalHoldManage);

        documents.MapPost("/{id:guid}/dispose", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
            {
                var expected = Concurrency.ExpectedVersion(http.Request);
                return expected.IsFailure
                    ? Problems.For(expected.Error)
                    : (await dispatcher.Send(new DisposeDocument(id, expected.Value), ct)).ToHttp(version =>
                    {
                        Concurrency.SetETag(http, version);
                        return Results.NoContent();
                    });
            })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("DocumentsDispose")
            .WithSummary("Dispose of a document after its retention date, when no legal hold applies")
            .RequirePermission(DocumentPermissions.DocumentDispose);
    }

    private static IResult Created(HttpContext http, DocumentView view)
    {
        Concurrency.SetETag(http, 1);
        return Results.Created($"/api/v1/documents/documents/{view.Id}", view with { RowVersion = 1 });
    }
}
