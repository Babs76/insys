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

    /// <summary>Every record type a process or task can carry: the approval reference types the modules declared and the process-only types.</summary>
    public static IReadOnlyList<string> All =>
        [.. ApprovalKinds.BusinessRefTypes.Union(ProcessOnly.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The screen that opens the record a process or task is about (BR-005), or null when the type is not declared.</summary>
    public static string? ScreenFor(string businessRefType) => ApprovalKinds.ScreenFor(businessRefType) ?? ProcessOnly.GetValueOrDefault(businessRefType);

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
