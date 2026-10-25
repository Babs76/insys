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
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Workflow.Api;

/// <summary>
/// The Workflow module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class WorkflowFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("WORKFLOW_APPROVALS", "WORKFLOW", "Approvals & authority limits", "workflow/approvals", "WorkflowListApprovals", "WorkflowGetApproval")
        .Action("WORKFLOW_APPROVE", "WORKFLOW_APPROVALS", "WorkflowApprove")
        .Action("WORKFLOW_REJECT", "WORKFLOW_APPROVALS", "WorkflowReject")
        .Screen("WORKFLOW_PROCESSES", "WORKFLOW", "Processes", "workflow/processes", "WorkflowListProcesses", "WorkflowGetProcess")
        .Action("WORKFLOW_START_PROCESS", "WORKFLOW_PROCESSES", "WorkflowStartProcess")
        .Action("WORKFLOW_SUSPEND_PROCESS", "WORKFLOW_PROCESSES", "WorkflowSuspendProcess")
        .Action("WORKFLOW_RESUME_PROCESS", "WORKFLOW_PROCESSES", "WorkflowResumeProcess")
        .Action("WORKFLOW_CANCEL_PROCESS", "WORKFLOW_PROCESSES", "WorkflowCancelProcess")
        .Action("WORKFLOW_ADD_CASE_NOTE", "WORKFLOW_PROCESSES", "WorkflowAddCaseNote")
        .Action("WORKFLOW_ASSIGN_TASK", "WORKFLOW_PROCESSES", "WorkflowAssignTask")
        .Action("WORKFLOW_COMPLETE_TASK", "WORKFLOW_PROCESSES", "WorkflowCompleteTask")
        .Action("WORKFLOW_ESCALATE_TASK", "WORKFLOW_PROCESSES", "WorkflowEscalateTask")
        .Screen("WORKFLOW_TASKS", "WORKFLOW", "Tasks & queues", "workflow/tasks", "WorkflowListOpenTasks")
            .Functions,
    ];
}
