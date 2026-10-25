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
using System.ComponentModel.DataAnnotations;
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
    public static void Register(StaticDataDescriptor descriptor)
    {
        Lifecycles[descriptor.EntityType] = descriptor.Lifecycle;
        Codes_[descriptor.EntityCode] = 0;
    }

    private static readonly ConcurrentDictionary<string, byte> Codes_ = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<(Type Type, string Property), (string Description, string? Format)> Fields = new();

    /// <summary>
    /// Describes a field of a generated type (a static data record and its input), which has no documentation comments of
    /// its own: the OpenAPI documents give it this description, and the format when one is given (for example duration).
    /// </summary>
    public static void Describe(Type type, string property, string description, string? format = null) => Fields[(type, property)] = (description, format);

    /// <summary>The entity codes of the static data tables mapped so far, which static data audit events name as their target type.</summary>
    public static IEnumerable<string> EntityCodes => Codes_.Keys;

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

        // Approval types and reference types the modules declared (BR-005).
        if (context.JsonPropertyInfo?.AttributeProvider is MemberInfo kindMember)
        {
            if (kindMember.GetCustomAttribute<Contracts.Approvals.ApprovalTypeFieldAttribute>() is not null && Contracts.Approvals.ApprovalKinds.ApprovalTypes is { Count: > 0 } types)
            {
                SetEnum(schema, types);
            }
            else if (kindMember.GetCustomAttribute<Contracts.Approvals.BusinessRefTypeFieldAttribute>() is not null
                     && Contracts.Approvals.ApprovalKinds.BusinessRefTypes is { Count: > 0 } refTypes)
            {
                SetEnum(schema, refTypes);
            }
        }

        // AllowedValues: for contracts that reference no INSYS building block but BuildingBlocks.Contracts.
        if (context.JsonPropertyInfo?.AttributeProvider is MemberInfo allowedMember && allowedMember.GetCustomAttribute<AllowedValuesAttribute>() is { } allowed)
        {
            SetEnum(schema, allowed.Values.Select(v => v?.ToString()!).Where(v => v is not null));
        }

        foreach (var ((type, name), (description, format)) in Fields)
        {
            if (type == context.JsonTypeInfo.Type && schema.Properties?.TryGetValue(name, out var field) == true && field is OpenApiSchema described)
            {
                described.Description = description;
                described.Format = format ?? described.Format;
            }
        }

        // Fields marked AlwaysPresent: in every response, null or a value.
        var present = context.JsonTypeInfo.Properties.Where(prop => prop.AttributeProvider is MemberInfo m && m.GetCustomAttribute<Contracts.AlwaysPresentAttribute>() is not null)
            .Select(prop => prop.Name).ToList();
        if (present.Count > 0 && schema.Properties is not null)
        {
            schema.Required = new HashSet<string>(schema.Required ?? new HashSet<string>(), StringComparer.Ordinal);
            foreach (var name in present.Where(schema.Properties.ContainsKey))
            {
                schema.Required.Add(name);
            }
        }

        // A record's latest rejection (OQ-113) is always present: null when its approvals never ended without being granted.
        if (schema.Properties?.ContainsKey("latestRejection") == true)
        {
            schema.Required = new HashSet<string>(schema.Required ?? new HashSet<string>(), StringComparer.Ordinal) { "latestRejection" };
        }

        if (context.JsonPropertyInfo?.AttributeProvider is MemberInfo monthMember && monthMember.GetCustomAttribute<YearMonthAttribute>() is not null)
        {
            schema.Pattern = YearMonthAttribute.Pattern;
        }

        // The figure codes every module declared (DashboardFigureCodes).
        if (context.JsonTypeInfo.Type == typeof(Application.DashboardFigure) && schema.Properties?.TryGetValue("code", out var code) == true
            && code is OpenApiSchema codeSchema && Application.DashboardFigureCodes.All is { Count: > 0 } figures)
        {
            SetEnum(codeSchema, figures);
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
