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

    private static readonly ConcurrentDictionary<string, string> UnscopedReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// The business reference type's approvals are deliberately not scoped (F103, OQ-176), and why: its records belong to no product or branch (static
    /// data, a party, a user). Every other declared type has a scope source in its module, which a test checks.
    /// </summary>
    public static void Unscoped(string businessRefType, string reason) => UnscopedReasons[businessRefType] = reason;

    /// <summary>The types declared unscoped, with their reasons.</summary>
    public static IReadOnlyDictionary<string, string> UnscopedTypes => UnscopedReasons;

    public static IReadOnlyList<string> ApprovalTypes => [.. Types.Keys.Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> BusinessRefTypes => [.. Screens.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The screen that opens a record of the reference type, or null when none was declared.</summary>
    public static string? ScreenFor(string businessRefType) => Screens.GetValueOrDefault(businessRefType);

    private static readonly ConcurrentDictionary<string, SubjectRead> SubjectReads = new(StringComparer.Ordinal);

    /// <summary>
    /// DE-019, DE-020: the read operation that opens the record an approval of the reference type is about, by the approval's
    /// business ref ID. <paramref name="pathTemplate"/> is the operation's path as the OpenAPI document gives it (from /api/v1/), with {id} where the business ref
    /// ID goes (none when the record is the tenant's only one, as its governance settings are). Every declared type has one,
    /// which ApprovalKindsTests checks against the host's routes.
    /// </summary>
    /// <param name="operationId">The operation's ID in the OpenAPI document (its endpoint name), for example PartyGetBankAccount.</param>
    public static void OpensWith(string businessRefType, string operationId, string pathTemplate) =>
        SubjectReads[businessRefType] = new SubjectRead(operationId, pathTemplate);

    /// <summary>The read operation that opens a record of the reference type, or null when none was declared.</summary>
    public static SubjectRead? ReadFor(string businessRefType) => SubjectReads.GetValueOrDefault(businessRefType);

    private static readonly ConcurrentDictionary<string, string> NoSubjectReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// DE-020: the reference type deliberately has no read that opens its record, and why (a discarded static data draft: nothing is left,
    /// and only an informational task, never an approval, carries the type). Its approvals' subjectOperation and subjectPath are null.
    /// ApprovalKindsTests accepts this in place of <see cref="OpensWith"/>.
    /// </summary>
    public static void OpensNothing(string businessRefType, string reason) => NoSubjectReasons[businessRefType] = reason;

    /// <summary>The types declared with no read that opens their record, with their reasons.</summary>
    public static IReadOnlyDictionary<string, string> TypesOpeningNothing => NoSubjectReasons;
}

/// <summary>The read operation that opens an approval's record (DE-020): its operation ID and its path, with {id} for the business ref ID.</summary>
public sealed record SubjectRead(string OperationId, string PathTemplate)
{
    public const string IdPlaceholder = "{id}";

    /// <summary>The path with the business ref ID filled in.</summary>
    public string PathFor(Guid businessRefId) => PathTemplate.Replace(IdPlaceholder, businessRefId.ToString(), StringComparison.Ordinal);
}

/// <summary>The field holds an approval type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ApprovalTypeFieldAttribute : Attribute;

/// <summary>The field holds an approval's business reference type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BusinessRefTypeFieldAttribute : Attribute;
