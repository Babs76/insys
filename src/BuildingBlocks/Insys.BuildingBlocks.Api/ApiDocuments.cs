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
/// OpenAPI schema names (GAP-031). Every module type is named with its module, unless its name already starts with it:
/// General's PolicyView is GeneralPolicyView, Life's LifePolicyView stays as it is. A generic type is named from its
/// arguments' names (CursorPageOfIdentityUserView). Types outside the modules (ProblemDetails, CursorPage) keep their
/// own names. Two types never share a name: the generator would keep one schema for both and lose the other's shape,
/// so <see cref="For"/> throws instead.
/// </summary>
public static class SchemaNames
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type> Assigned = new(StringComparer.Ordinal);

    public static string? For(System.Text.Json.Serialization.Metadata.JsonTypeInfo type)
    {
        var name = Microsoft.AspNetCore.OpenApi.OpenApiOptions.CreateDefaultSchemaReferenceId(type);
        if (name is null)
        {
            return null;
        }

        var clr = Nullable.GetUnderlyingType(type.Type) ?? type.Type;
        var id = Id(clr) ?? name;
        var owner = Assigned.GetOrAdd(id, clr);
        if (owner != clr)
        {
            throw new InvalidOperationException($"Schema ID {id} would name both {owner.FullName} and {clr.FullName} (GAP-031).");
        }

        return id;
    }

    /// <summary>The schema ID of an INSYS type, or null where the generator's default applies.</summary>
    public static string? Id(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && !type.IsGenericTypeDefinition && type.Namespace?.StartsWith("Insys.", StringComparison.Ordinal) == true)
        {
            var definition = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return definition + "Of" + string.Join("And", type.GetGenericArguments().Select(a => Id(a) ?? a.Name));
        }

        if (Module(type) is not { } module || type.IsGenericType)
        {
            return null;
        }

        var name = type.IsNested ? type.DeclaringType!.Name + type.Name : type.Name;
        return name.StartsWith(module, StringComparison.Ordinal) ? name : module + name;
    }

    /// <summary>The module of an INSYS type (Insys.&lt;Module&gt;.…), or null outside the modules.</summary>
    public static string? Module(Type type) =>
        type.Namespace?.Split('.') is ["Insys", var module, ..] && module is not ("BuildingBlocks" or "Api") ? module : null;
}
