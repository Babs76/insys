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
using Microsoft.AspNetCore.Builder;

namespace Insys.BuildingBlocks.Api;

/// <summary>
/// What an operation sets off that completes after it returns (GAP-027). The operation commits its own change before it
/// responds, so it is not 202: the effects listed here follow through integration events, normally within a few seconds
/// (the outbox relay polls every Outbox:PollInterval, 2 seconds by default), and are seen by reading again.
/// </summary>
public sealed record EventualEffectsMetadata(string Effects);

public static class EventualEffectsExtensions
{
    public const string Extension = "x-eventual-effects";

    /// <summary>Declares the effects that follow the operation asynchronously, and how to see them.</summary>
    public static TBuilder WithEventualEffects<TBuilder>(this TBuilder builder, string effects)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new EventualEffectsMetadata(effects));
}

/// <summary>
/// Values a free-text parameter is known to take, which the documents list as x-known-values (GAP-033): not an enum,
/// because other values are allowed, but the ones INSYS itself uses.
/// </summary>
public sealed record KnownValuesMetadata(string Parameter, IReadOnlyList<string> Values, string Description);

public static class KnownValuesExtensions
{
    public const string Extension = "x-known-values";

    public static TBuilder WithKnownValues<TBuilder>(this TBuilder builder, string parameter, IReadOnlyList<string> values, string description)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new KnownValuesMetadata(parameter, values, description));
}


/// <summary>
/// A second body a response may carry instead of the one Produces declares (OQ-196): the documents give that status's JSON body as oneOf the two.
/// </summary>
public sealed record AlternativeResponseMetadata(int StatusCode, Type Type);

public static class AlternativeResponseExtensions
{
    /// <summary>Declares that the response with <paramref name="statusCode"/> may carry a <typeparamref name="T"/> instead of its declared body.</summary>
    public static TBuilder ProducesAlternative<T, TBuilder>(this TBuilder builder, int statusCode)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AlternativeResponseMetadata(statusCode, typeof(T)));
}
