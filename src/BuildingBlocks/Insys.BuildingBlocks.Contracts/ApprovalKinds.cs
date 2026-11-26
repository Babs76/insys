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

    private static readonly ConcurrentDictionary<string, string> ReadsByRecord = new(StringComparer.Ordinal);

    /// <summary>
    /// OQ-203 (Ade, 26 November 2026): the record of the reference type is opened by a read that depends on the record itself, not on its ID alone, so no one
    /// operation is declared with <see cref="OpensWith"/>: a discarded static data draft or import links to its own table's page of versions not in force
    /// (each table has its own operation), which the record's task carries (subjectOperation and subjectPath of the task). <paramref name="how"/> says which
    /// read. Only informational tasks, never an approval, carry these types, so an approval's subjectOperation and subjectPath are null for them.
    /// ApprovalKindsTests accepts this in place of <see cref="OpensWith"/>.
    /// </summary>
    public static void OpensByRecord(string businessRefType, string how) => ReadsByRecord[businessRefType] = how;

    /// <summary>The types whose read depends on the record (<see cref="OpensByRecord"/>), with which read.</summary>
    public static IReadOnlyDictionary<string, string> TypesOpenedByRecord => ReadsByRecord;
}

/// <summary>The read operation that opens an approval's record (DE-020): its operation ID and its path, with {id} for the business ref ID.</summary>
public sealed record SubjectRead(string OperationId, string PathTemplate)
{
    public const string IdPlaceholder = "{id}";

    /// <summary>The path with the business ref ID filled in.</summary>
    public string PathFor(Guid businessRefId) => PathTemplate.Replace(IdPlaceholder, businessRefId.ToString(), StringComparison.Ordinal);
}

/// <summary>
/// The record types a process and its tasks can be about (BR-170, GAP-179): every approval reference type (<see cref="ApprovalKinds"/>), and the
/// types only processes use, each with the screen that opens its record (BR-005). Workflow declares the process-only types at start-up from its one
/// list and refuses to open a process about an undeclared type; the OpenAPI documents list the union as the enum of a task's and a process's record type,
/// with <see cref="Undeclared"/> in the views.
/// </summary>
public static class TaskRecordTypes
{
    /// <summary>
    /// What a view shows for a stored record type that is neither declared nor has a declared equivalent (Ade, 26 November 2026): a process started
    /// through the API before BR-170 narrowed the start request. It opens no screen, and no process can be started about it; the view keeps the stored
    /// value in <c>storedBusinessRefType</c>, and the stored row is unchanged.
    /// </summary>
    public const string Undeclared = "UNDECLARED";

    private static readonly ConcurrentDictionary<string, string> Screens = new(StringComparer.Ordinal);

    /// <param name="screenFunction">The SCREEN function that opens the record (ADR-026).</param>
    public static void Declare(string businessRefType, string screenFunction)
    {
        if (businessRefType == Undeclared)
        {
            throw new InvalidOperationException($"{Undeclared} is what a view shows for an undeclared type; it can't be declared as a record type.");
        }

        Screens[businessRefType] = screenFunction;
    }

    /// <summary>The types declared for processes only, which no approval names.</summary>
    public static IReadOnlyList<string> ProcessOnly => [.. Screens.Keys.Except(ApprovalKinds.BusinessRefTypes, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Every record type a process can be started about: the approval reference types and the process-only types (the start request's enum).</summary>
    public static IReadOnlyList<string> Startable => [.. ApprovalKinds.BusinessRefTypes.Union(Screens.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Every record type a process or task view can show: <see cref="Startable"/> and <see cref="Undeclared"/>.</summary>
    public static IReadOnlyList<string> All => [.. Startable.Append(Undeclared).Order(StringComparer.Ordinal)];

    /// <summary>The screen that opens a record of the type, or null when the type is not declared (and for <see cref="Undeclared"/>).</summary>
    public static string? ScreenFor(string businessRefType) => ApprovalKinds.ScreenFor(businessRefType) ?? Screens.GetValueOrDefault(businessRefType);
}

/// <summary>The field holds an approval type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ApprovalTypeFieldAttribute : Attribute;

/// <summary>The field holds an approval's business reference type: the OpenAPI documents give it the enum of declared types (BR-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BusinessRefTypeFieldAttribute : Attribute;

/// <summary>
/// The field holds a process's or task's record type: the OpenAPI documents give it the enum of <see cref="TaskRecordTypes.All"/> (BR-170), or of
/// <see cref="TaskRecordTypes.Startable"/> when <see cref="Startable"/> is set (a request that starts a process). Approval views keep
/// <see cref="BusinessRefTypeFieldAttribute"/>, as an approval is only ever about an approval reference type.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TaskRecordTypeFieldAttribute : Attribute
{
    /// <summary>The field names the type to start a process about: <see cref="TaskRecordTypes.Undeclared"/> is not offered.</summary>
    public bool Startable { get; init; }
}
