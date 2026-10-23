/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
namespace Insys.BuildingBlocks.Api;

/// <summary>Marks a static data endpoint (generated tables and their governance), which belongs in the static-data document.</summary>
public sealed record StaticDataEndpointMetadata;

/// <summary>
/// The three OpenAPI documents (Phase 1 review, item 4). Every endpoint is in exactly one:
/// platform operator endpoints first, then static data, and everything else in core.
/// </summary>
public static class ApiDocuments
{
    public const string Core = "core";
    public const string StaticData = "static-data";
    public const string PlatformOperator = "platform-operator";

    public static readonly IReadOnlyList<(string Name, string Title, string Description)> All =
    [
        (Core, "INSYS Core API",
            "Multi-tenant insurance administration platform. The tenant comes from the bearer token's tenant_id claim."),
        (StaticData, "INSYS Core static data API",
            "Generated endpoints for the static data tables of every module, and their governance: change requests, approval and imports."),
        (PlatformOperator, "INSYS Core platform operator API",
            "For the platform operator tenant only: tenant administration and global static data (scopes G and GT)."),
    ];

    public static string For(IEnumerable<object> endpointMetadata)
    {
        var metadata = endpointMetadata as IList<object> ?? endpointMetadata.ToList();
        return metadata.OfType<PlatformOperatorEndpointMetadata>().Any() ? PlatformOperator
            : metadata.OfType<StaticDataEndpointMetadata>().Any() ? StaticData
            : Core;
    }
}

/// <summary>
/// OpenAPI schema names (GAP-008). A type's own name, unless another INSYS type has the same name (Life's and General's
/// ProposalStatus, say): then both are prefixed with their module, as LifeProposalStatus and GeneralProposalStatus.
/// Without this the document generator keeps one schema per name, and the other module's shape silently disappears.
/// </summary>
public static class SchemaNames
{
    private static readonly Lazy<HashSet<string>> Ambiguous = new(() => AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => a.GetName().Name?.StartsWith("Insys.", StringComparison.Ordinal) == true)
        .SelectMany(a =>
        {
            try
            {
                return a.GetExportedTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException e)
            {
                return e.Types.OfType<Type>();
            }
        })
        .Where(t => !t.IsGenericType && !t.IsNested && Module(t) is not null)
        .GroupBy(t => t.Name, StringComparer.Ordinal)
        .Where(g => g.Select(Module).Distinct(StringComparer.Ordinal).Count() > 1)
        .Select(g => g.Key)
        .ToHashSet(StringComparer.Ordinal));

    public static string? For(System.Text.Json.Serialization.Metadata.JsonTypeInfo type)
    {
        var name = Microsoft.AspNetCore.OpenApi.OpenApiOptions.CreateDefaultSchemaReferenceId(type);
        var clr = Nullable.GetUnderlyingType(type.Type) ?? type.Type;
        return name is not null && name == clr.Name && Ambiguous.Value.Contains(clr.Name) && Module(clr) is { } module ? module + name : name;
    }

    /// <summary>The module of an INSYS type (Insys.&lt;Module&gt;.…), or null outside the modules.</summary>
    private static string? Module(Type type) =>
        type.Namespace?.Split('.') is ["Insys", var module, ..] && module != "BuildingBlocks" ? module : null;
}
