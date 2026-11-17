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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Documents.Application;
using Insys.Documents.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Documents.Infrastructure;

/// <summary>A document read for the scope's user, as its endpoints would (D-16); a service scope is refused.</summary>
internal sealed class DocumentReader(IServiceProvider services, ExecutionScope execution) : IDocumentReader
{
    // The endpoints' function (DocumentsFunctions: the DOCUMENTS screen covers the document and its content).
    private const string Function = "DOCUMENTS";

    public async Task<DocumentRead> Facts(Guid documentId, CancellationToken cancellationToken)
    {
        if (ActingUser.Check(execution, DocumentPermissions.DocumentRead, [Function], isRead: true) is { } refused)
        {
            return new DocumentRead(null, null, refused.Code, refused.Message);
        }

        var view = await services.GetRequiredService<IDispatcher>().Query(new GetDocument(documentId), cancellationToken);
        return view.IsFailure
            ? new DocumentRead(null, null, view.Error.Code, view.Error.Message)
            : new DocumentRead(new DocumentFacts(view.Value.Id, view.Value.FileName, view.Value.MimeType, view.Value.SizeBytes, view.Value.Sha256, view.Value.ScanStatus), null);
    }

    public async Task<DocumentRead> Read(Guid documentId, string purpose, CancellationToken cancellationToken)
    {
        var facts = await Facts(documentId, cancellationToken);
        if (!facts.Succeeded)
        {
            return facts;
        }

        var content = await services.GetRequiredService<IDispatcher>().Send(new ReadDocumentContent(documentId, purpose), cancellationToken);
        return content.IsFailure ? new DocumentRead(facts.Facts, null, content.Error.Code, content.Error.Message) : facts with { Content = content.Value.Content };
    }
}
