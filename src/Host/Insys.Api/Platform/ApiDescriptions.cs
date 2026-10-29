/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Api.Host
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
using System.Collections.Concurrent;
using Insys.BuildingBlocks.Api;

namespace Insys.Api.Platform;

/// <summary>
/// The API descriptions (Ade, 29 October 2026): the committed documents in docs/api, embedded at build and served as they
/// are, never built on request. In Development and Testing anyone may read them; anywhere else, only a signed-in user. The
/// build still generates the documents from the code (obj/openapi), and OpenApiContractTests keeps the committed copies equal.
/// </summary>
public static class ApiDescriptions
{
    private static readonly ConcurrentDictionary<string, byte[]?> Documents = new(StringComparer.Ordinal);

    public static IEndpointConventionBuilder MapApiDescriptions(this WebApplication app)
    {
        var endpoint = app.MapGet("/openapi/{documentName}.json", (string documentName) =>
                Read(documentName) is { } document ? Results.Bytes(document, "application/json") : Results.NotFound())
            .ExcludeFromDescription();
        return InsysAuthenticationOptions.AllowsDevelopmentKey(app.Environment) ? endpoint.AllowAnonymous() : endpoint.RequireAuthorization();
    }

    /// <summary>The committed document by name (core, static-data, platform-operator), or null for any other name.</summary>
    public static byte[]? Read(string documentName) =>
        ApiDocuments.All.Any(d => d.Name == documentName)
            ? Documents.GetOrAdd(documentName, name =>
            {
                using var stream = typeof(ApiDescriptions).Assembly.GetManifestResourceStream($"openapi-{name}.json");
                if (stream is null)
                {
                    return null;
                }

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            })
            : null;
}
