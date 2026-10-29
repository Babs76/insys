/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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

namespace Insys.BuildingBlocks.Contracts.Approvals;

/// <summary>
/// The approval types and business reference types the modules use, with the screen (its function code) that opens the
/// record a reference type names (BR-005). Each module declares its own at start-up; the OpenAPI documents list them as
/// enums, and an approval's view names the screen. A type a module requests without declaring it fails ApprovalKindsTests.
/// </summary>
public static class ApprovalKinds
{
    private static readonly ConcurrentDictionary<string, string> Screens = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Types = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Features = new(StringComparer.Ordinal);

    /// <summary>The marker of approval types raised only in the platform operator tenant (a new tenant's first administrator).</summary>
    public const string PlatformOperator = "PLATFORM_OPERATOR";

    /// <summary>
    /// The approval types are raised only by a tenant entitled to the feature (LIFE, GENERAL, HEALTH), or only in the
    /// operator tenant (<see cref="PlatformOperator"/>): onboarding readiness asks for authority over the types a tenant can raise.
    /// </summary>
    public static void RaisedWith(string feature, params string[] approvalTypes)
    {
        foreach (var type in approvalTypes)
        {
            Features[type] = feature;
        }
    }

    /// <summary>The feature an approval type needs, or null when any tenant may raise it.</summary>
    public static string? FeatureOf(string approvalType) => Features.GetValueOrDefault(approvalType);

    /// <param name="screenFunction">The SCREEN function that opens the record (ADR-026), for example PAYMENT_PAYOUT_BATCHES.</param>
    public static void Declare(string businessRefType, string screenFunction, params string[] approvalTypes)
    {
        Screens[businessRefType] = screenFunction;
        foreach (var type in approvalTypes)
        {
            Types[type] = 0;
        }
    }

    public static IReadOnlyList<string> ApprovalTypes => [.. Types.Keys.Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> BusinessRefTypes => [.. Screens.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The screen that opens a record of the reference type, or null when none was declared.</summary>
    public static string? ScreenFor(string businessRefType) => Screens.GetValueOrDefault(businessRefType);
}

/// <summary>The field holds an approval type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ApprovalTypeFieldAttribute : Attribute;

/// <summary>The field holds an approval's business reference type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BusinessRefTypeFieldAttribute : Attribute;
