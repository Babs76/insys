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
