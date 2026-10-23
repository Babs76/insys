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
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// State and code fields as enums in the OpenAPI documents (GAP-008). Fields typed as a C# enum need nothing: they are
/// serialised by name and the generator declares them. This adds the string fields whose codes are fixed elsewhere:
/// <see cref="OneOfAttribute"/> on a property, and a static data record's recordStatus, from its table's lifecycle.
/// </summary>
public static class StateEnums
{
    private static readonly ConcurrentDictionary<Type, StaticLifecycle> Lifecycles = new();

    /// <summary>Records the lifecycle of a static data table as its endpoints are mapped.</summary>
    public static void Register(StaticDataDescriptor descriptor) => Lifecycles[descriptor.EntityType] = descriptor.Lifecycle;

    /// <summary>Every record_status code of a lifecycle, in lifecycle order.</summary>
    public static IReadOnlyList<string> Codes(StaticLifecycle lifecycle) =>
        new[] { lifecycle.Draft }.Concat(lifecycle.Submitted).Append(lifecycle.Approved).Append(lifecycle.Active).Concat(lifecycle.InForce)
            .Append(lifecycle.Deprecated).Append(lifecycle.Retired).Distinct(StringComparer.Ordinal).ToList();

    public static Task Apply(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonPropertyInfo?.AttributeProvider is MemberInfo member && member.GetCustomAttribute<OneOfAttribute>() is { } oneOf)
        {
            var target = schema.Type?.HasFlag(JsonSchemaType.Array) == true && schema.Items is OpenApiSchema items ? items : schema;
            SetEnum(target, oneOf.Values);
        }

        if (Lifecycles.TryGetValue(context.JsonTypeInfo.Type, out var lifecycle)
            && schema.Properties?.TryGetValue("recordStatus", out var property) == true && property is OpenApiSchema status)
        {
            SetEnum(status, Codes(lifecycle));
        }

        return Task.CompletedTask;
    }

    private static void SetEnum(OpenApiSchema schema, IEnumerable<string> values)
    {
        var codes = values.Select(v => (JsonNode)JsonValue.Create(v)).ToList();
        if (schema.Type?.HasFlag(JsonSchemaType.Null) == true)
        {
            codes.Add(null!);
        }

        schema.Enum = codes;
    }
}
