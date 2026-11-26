/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Workflow
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
using Insys.BuildingBlocks.Contracts.Approvals;
using Insys.BuildingBlocks.Domain;
using Insys.Workflow.Domain.Processes;

namespace Insys.Workflow.Application;

/// <summary>
/// BR-170 (GAP-179): the record types a process and its tasks can be about. They are the approval reference types (ApprovalKinds, each module's own,
/// BR-005) and the types below, which only processes use, each with the screen that opens its record. This list is the one source: the task's screen
/// (<see cref="ScreenFor"/>), the contract's enum (<see cref="TaskRecordTypes"/>, declared at start-up) and the guard every process start goes
/// through (<see cref="Start"/>). A process about a type on neither list is refused, so the contract can't miss one.
/// </summary>
public static class ProcessRecordTypes
{
    /// <summary>The types only processes use, with the SCREEN function that opens the record (ADR-026). None is an approval reference type.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProcessOnly = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // A parked delivery escalated to a person (DE-028).
        [Integration.Contracts.ParkedDeliveryEscalated.BusinessRefType] = "INTEGRATION_PARKED_DELIVERIES",
        // A fraud indicator the claims evaluation skipped, reviewed on its configuration table (the reference is the indicator code's stable ID).
        [OpenConfigurationReviewTask.FraudIndicatorRefType] = "SD_SHARED_CLAIMS_FRAMEWORK_VIEW",
        // A party's match review and its due diligence reviews (DE-032).
        [OpenPartyMatchReviewTask.BusinessRefType] = "PARTIES",
        // F059, F064: a data-subject request, and a party past retention.
        [PartyComplianceTasks.RequestRefType] = "PARTY_DATA_SUBJECT_REQUESTS",
        [PartyComplianceTasks.RetentionRefType] = "PARTIES",
        // An accepted proposal waiting to be issued.
        [Life.Contracts.LifeProposalTasks.BusinessRefType] = "LIFE_PROPOSALS",
        [General.Contracts.GeneralProposalTasks.BusinessRefType] = "GENERAL_PROPOSALS",
        // A General quote referred to an underwriter, an aggregation zone over its limit, a retention case.
        [General.Contracts.GeneralReferrals.BusinessRefType] = "GENERAL_QUOTES",
        [General.Contracts.AggregationZones.BusinessRefType] = "GENERAL_AGGREGATION_ALERTS",
        [GeneralRetentionTasks.BusinessRefType] = "GENERAL_RETENTION_CASES",
        // Health: a membership in arrears, a provider measure breached, a programme review, an overdue care activity, a recovery review.
        [Health.Contracts.HealthMembershipTasks.BusinessRefType] = "HEALTH_MEMBERSHIPS",
        [ProviderMeasureReviewTasks.BusinessRefType] = "HEALTH_PROVIDER_PERFORMANCE",
        [ProgrammeReviewTasks.BusinessRefType] = "HEALTH_PROGRAMME_ENROLMENTS",
        [CareActivityFollowUpTasks.BusinessRefType] = "HEALTH_COMPLEX_CASES",
        [RecoveryReviewTasks.BusinessRefType] = "HEALTH_RECOVERIES",
        // A service case's process (addenda/cases.md): the case operations, WorkflowGetCase among them, sit under the complaints screen whatever the family.
        [CaseOpening.BusinessRefType] = "SERVICE_COMPLAINTS",
    };

    /// <summary>
    /// Stored record types that are no longer declared, each with its declared equivalent: the same record, opened by the same read (Ade, 26 November
    /// 2026). A view shows the equivalent, its screen, and the stored value in <c>storedBusinessRefType</c>; the stored row is unchanged. A stored type
    /// that is neither declared nor here is shown as <see cref="TaskRecordTypes.Undeclared"/>. Empty: no start path in the code since process starts
    /// existed stored a type that was later renamed or dropped (BR-170 contract note); a rename adds its old value here. Decided (Ade, 26 November 2026,
    /// BR-170 decision 1): the table is approved, no stored type has a current equivalent, so it stays empty. A key that folds to a declared type
    /// (<see cref="Folded(string)"/>) is never reached: folding comes first.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Equivalents = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every record type a process can be started about: the approval reference types the modules declared and the process-only types.</summary>
    public static IReadOnlyList<string> All =>
        [.. ApprovalKinds.BusinessRefTypes.Union(ProcessOnly.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The characters trimmed from a stored value before it is folded (<see cref="Folded(string)"/>): spaces, tabs and line breaks. The list filter
    /// trims the same ones (<see cref="FoldingPattern"/>), so a view and a filter always agree on which declared type a stored value folds to.
    /// </summary>
    public static readonly char[] FoldedWhitespace = [' ', '\t', '\n', '\r'];

    /// <summary>
    /// The record type a view shows for a stored one, and the stored value when they differ (Ade, 26 November 2026, BR-170 decisions 1 and 2), in this
    /// order: a declared type as it is; a value that matches a declared type after trimming and ignoring letter case (" general_claim ") as that
    /// declared type (<see cref="Folded(string)"/>); an older one as its equivalent (<see cref="Equivalents"/>, empty: the table is approved and no
    /// stored type has a current equivalent); any other as <see cref="TaskRecordTypes.Undeclared"/>. Stored rows are never changed. A value that folds
    /// to UNDECLARED itself stays UNDECLARED, since UNDECLARED is not a declared type.
    /// </summary>
    public static (string Type, string? Stored) Shown(string stored) => Shown(stored, Equivalents);

    /// <summary><see cref="Shown(string)"/> with a given map of equivalents (tests).</summary>
    public static (string Type, string? Stored) Shown(string stored, IReadOnlyDictionary<string, string> equivalents) =>
        IsDeclared(stored) ? (stored, null)
        : Folded(stored) is { } declared ? (declared, stored)
        : equivalents.TryGetValue(stored, out var equivalent) && IsDeclared(equivalent) ? (equivalent, stored)
        : (TaskRecordTypes.Undeclared, stored);

    /// <summary>
    /// The declared type a stored value matches once trimmed of <see cref="FoldedWhitespace"/> and compared ordinally ignoring case (Ade, 26 November
    /// 2026), or null. Never UNDECLARED, which is not declared.
    /// </summary>
    public static string? Folded(string stored)
    {
        var trimmed = stored.Trim(FoldedWhitespace);
        return All.FirstOrDefault(d => string.Equals(d, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What a process list filter by record type matches (Ade, 26 November 2026): the stored values in <see cref="RecordTypeFilter.Exact"/>, and, when
    /// <see cref="RecordTypeFilter.Folding"/> is set, the stored values that match that PostgreSQL regular expression; with
    /// <see cref="RecordTypeFilter.Excluding"/>, every stored value matching neither.
    /// <list type="bullet">
    /// <item>A declared type: the type itself, the stored values that fold to it (<see cref="Folded(string)"/>), and the older values whose equivalent
    /// it is.</item>
    /// <item><see cref="TaskRecordTypes.Undeclared"/>: every stored value a view shows as UNDECLARED, that is neither in <see cref="Known"/> nor folds
    /// to a declared type.</item>
    /// <item>Any other value (a stored undeclared one): exactly that value.</item>
    /// </list>
    /// </summary>
    public static RecordTypeFilter FilterFor(string filter) => FilterFor(filter, Equivalents);

    /// <summary><see cref="FilterFor(string)"/> with a given map of equivalents (tests).</summary>
    public static RecordTypeFilter FilterFor(string filter, IReadOnlyDictionary<string, string> equivalents)
    {
        if (filter == TaskRecordTypes.Undeclared)
        {
            return new([.. KnownWith(equivalents).Order(StringComparer.Ordinal)], FoldingPattern(All), Excluding: true);
        }

        return IsDeclared(filter)
            ? new([filter, .. equivalents.Where(e => e.Value == filter && Folded(e.Key) is null).Select(e => e.Key).Order(StringComparer.Ordinal)],
                FoldingPattern([filter]), Excluding: false)
            : new([filter], null, Excluding: false);
    }

    /// <summary>
    /// A PostgreSQL regular expression matching a value that equals one of the (ASCII) declared types once trimmed of <see cref="FoldedWhitespace"/>,
    /// ignoring letter case: each letter is a bracket of its two cases, so it doesn't depend on the database's locale, and it folds exactly what
    /// <see cref="Folded(string)"/> folds (ordinal ignore-case folds no non-ASCII letter to an ASCII one).
    /// </summary>
    public static string FoldingPattern(IEnumerable<string> declared)
    {
        var space = $"[{new string(FoldedWhitespace)}]*";
        var types = declared.Select(d => string.Concat(d.Select(c =>
            char.IsAsciiLetter(c) ? $"[{char.ToUpperInvariant(c)}{char.ToLowerInvariant(c)}]"
            : char.IsAsciiDigit(c) || c == '_' ? c.ToString()
            : throw new InvalidOperationException($"Record type {d} is not ASCII letters, digits and underscores (BR-170)."))));
        return $"^{space}(?:{string.Join('|', types)}){space}$";
    }

    /// <summary>
    /// The stored values a view shows exactly as something other than <see cref="TaskRecordTypes.Undeclared"/>: the declared types and their older
    /// equivalents (a value that folds to a declared type is matched by <see cref="FoldingPattern"/> instead).
    /// </summary>
    public static string[] Known => KnownWith(Equivalents);

    private static string[] KnownWith(IReadOnlyDictionary<string, string> equivalents) => [.. All, .. equivalents.Keys.Where(k => IsDeclared(equivalents[k]))];

    /// <summary>The screen that opens the record a process or task is about (BR-005), or null when the type is not declared.</summary>
    public static string? ScreenFor(string businessRefType) => ApprovalKinds.ScreenFor(businessRefType) ?? ProcessOnly.GetValueOrDefault(businessRefType);

    /// <summary>A process can be started about the type: it is declared, with a screen. <see cref="TaskRecordTypes.Undeclared"/> never is.</summary>
    public static bool IsDeclared(string businessRefType) => ScreenFor(businessRefType) is not null;

    /// <summary>Declares the process-only types for the OpenAPI documents (at start-up, beside the module's approval kinds).</summary>
    public static void Declare()
    {
        foreach (var (type, screen) in ProcessOnly)
        {
            TaskRecordTypes.Declare(type, screen);
        }
    }

    /// <summary>
    /// Starts a process (<see cref="ProcessInstance.StartProcess"/>) about a declared record type. Every process start in Workflow goes through this;
    /// an undeclared type is a programming error, so it throws: declare the type above, or as an approval reference type, with its screen.
    /// </summary>
    public static Result<ProcessInstance> Start(Guid id, ProcessDefinition definition, string businessRefType, Guid businessRefId, Func<string, TaskRouting> route,
        DateTimeOffset now) =>
        IsDeclared(businessRefType)
            ? ProcessInstance.StartProcess(id, definition, businessRefType, businessRefId, route, now)
            : throw new InvalidOperationException(
                $"Process {definition.ProcessCode} was started about record type {businessRefType}, which is not declared (BR-170): add it to ProcessRecordTypes.ProcessOnly with its screen.");
}

/// <summary>What a process list filter by record type matches: see <see cref="ProcessRecordTypes.FilterFor(string)"/>.</summary>
/// <param name="Exact">Stored values matched exactly.</param>
/// <param name="Folding">A PostgreSQL regular expression for the stored values that fold to the filter's declared type(s); null for none.</param>
/// <param name="Excluding">The filter matches every stored value matching neither (UNDECLARED).</param>
public sealed record RecordTypeFilter(string[] Exact, string? Folding, bool Excluding);
