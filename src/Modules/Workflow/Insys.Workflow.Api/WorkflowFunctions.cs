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
using Insys.Workflow.Application;
using Insys.Workflow.Contracts;

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
        .Screen("WORKFLOW_APPROVALS", "WORKFLOW", "Approvals & authority limits", "workflow/approvals", "WorkflowListApprovals", "WorkflowGetApproval", "WorkflowMyAuthority")
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

        // Cases (addenda/cases.md §9). An operation maps to one staff function, so the case operations sit under the
        // complaints screen; the other case screens are entries of the same case list, filtered by family or link.
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("SERVICE_COMPLAINTS", "CUSTOMER_SERVICE", "Complaints", "workflow/cases/complaints", "WorkflowListCases", "WorkflowGetCase", "WorkflowGetCaseRedress",
            "WorkflowCasesSummary")
        .Action("CASE_REGISTER", "SERVICE_COMPLAINTS", "WorkflowOpenCase")
        .Action("CASE_ACKNOWLEDGE", "SERVICE_COMPLAINTS", "WorkflowAcknowledgeCase")
        .Action("CASE_UPDATE", "SERVICE_COMPLAINTS", ["WorkflowAddCaseMessage", "WorkflowStartCase", "WorkflowCaseAwaitComplainant", "WorkflowResumeCase"])
        .Action("CASE_LINK_RECORD", "SERVICE_COMPLAINTS", "WorkflowLinkCaseRecord")
        .Action("CASE_EXTEND_TIMELINE", "SERVICE_COMPLAINTS", "WorkflowExtendCaseTimeline")
        .Action("CASE_ESCALATE", "SERVICE_COMPLAINTS", "WorkflowEscalateCase")
        .Action("CASE_DECIDE", "SERVICE_COMPLAINTS", "WorkflowDecideCase")
        .Action("CASE_RECORD_REDRESS", "SERVICE_COMPLAINTS", "WorkflowRecordCaseRedress", privileged: true)
        .Action("CASE_REFER_TO_OMBUD", "SERVICE_COMPLAINTS", "WorkflowReferCaseToOmbud")
        .Action("CASE_RECORD_OMBUD_OUTCOME", "SERVICE_COMPLAINTS", "WorkflowRecordCaseOmbudOutcome")
        .Action("CASE_REOPEN", "SERVICE_COMPLAINTS", "WorkflowReopenCase", privileged: true)
        .Action("CASE_WITHDRAW", "SERVICE_COMPLAINTS", "WorkflowWithdrawCase")
        .Action("CASE_CLOSE", "SERVICE_COMPLAINTS", "WorkflowCloseCase")
        .Action("CASE_SHARE_WITH_BROKER", "SERVICE_COMPLAINTS", "WorkflowShareCaseWithBroker")
        // The complaints report alone, so a role holding only case_report.read keeps a function for it (ADR-026 conversion);
        // the cases dashboard (case.read) is on the complaints screen.
        .Screen("SERVICE_CASE_REPORTS", "CUSTOMER_SERVICE", "Case reporting", "workflow/cases/reports", "WorkflowComplaintsReport")
        .Job(CaseFunctions.Timelines, "CUSTOMER_SERVICE", "Background processing")
            .Functions,

        // OQ-109: deciding an appeal is privileged; the decide handler requires it for a case of family APPEAL.
        new FunctionDeclaration(CaseFunctions.DecideAppeal, FunctionKind.Action, "CUSTOMER_SERVICE", "Complaints", "SERVICE_COMPLAINTS", [Audiences.Staff], null, null,
            true, [], [CasePermissions.Decide]),
        CaseScreen("SERVICE_ENQUIRIES", "CUSTOMER_SERVICE", "Enquiries", "workflow/cases/enquiries", null),
        CaseScreen("SERVICE_DISPUTES", "CUSTOMER_SERVICE", "Disputes", "workflow/cases/disputes", null),
        CaseScreen("SERVICE_APPEALS", "CUSTOMER_SERVICE", "Appeals", "workflow/cases/appeals", null),
        CaseScreen("CLAIM_DISPUTES", "DISPUTES_APPEALS", "Claim disputes", "workflow/cases/claim-disputes", null),
        CaseScreen("CLAIM_APPEALS", "DISPUTES_APPEALS", "Claim appeals", "workflow/cases/claim-appeals", null),
        CaseScreen("HEALTH_CLAIM_APPEALS", "HEALTH_MEDICAL_CLAIMS", "Claim appeals", "workflow/cases/health-claim-appeals", Features.Health),
        CaseScreen("PROVIDER_DISPUTES", "HEALTH_PROVIDER_MANAGEMENT", "Provider disputes", "workflow/cases/provider-disputes", Features.Health),

        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_CASES", "CUSTOMER_PORTAL", "Complaints & enquiries", "portal/cases", "PortalListCases", "PortalGetCase")
        .Action("CUSTOMER_PORTAL_RAISE_CASE", "CUSTOMER_PORTAL_CASES", "PortalRaiseCase")
        .Action("CUSTOMER_PORTAL_CASE_MESSAGE", "CUSTOMER_PORTAL_CASES", "PortalAddCaseMessage")
        .Action("CUSTOMER_PORTAL_APPEAL_CASE", "CUSTOMER_PORTAL_CASES", "PortalAppealCase")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_CASES", "BROKER_PORTAL", "Complaints & enquiries", "portal/cases", "PortalListCases", "PortalGetCase")
        .Action("BROKER_PORTAL_RAISE_CASE", "BROKER_PORTAL_CASES", "PortalRaiseCase")
        .Action("BROKER_PORTAL_CASE_MESSAGE", "BROKER_PORTAL_CASES", "PortalAddCaseMessage")
        .Action("BROKER_PORTAL_APPEAL_CASE", "BROKER_PORTAL_CASES", "PortalAppealCase")
            .Functions,
        .. new FunctionMap([Audiences.Provider], Features.ProviderPortal)
        .Screen("PROVIDER_PORTAL_DISPUTES", "PROVIDER_PORTAL", "Disputes", "portal/cases", "PortalListCases", "PortalGetCase")
        .Action("PROVIDER_PORTAL_RAISE_DISPUTE", "PROVIDER_PORTAL_DISPUTES", "PortalRaiseCase")
        .Action("PROVIDER_PORTAL_DISPUTE_MESSAGE", "PROVIDER_PORTAL_DISPUTES", "PortalAddCaseMessage")
        .Action("PROVIDER_PORTAL_APPEAL_DISPUTE", "PROVIDER_PORTAL_DISPUTES", "PortalAppealCase")
            .Functions,
    ];

    /// <summary>A case screen reached through the case list (filtered by family or linked record): it requires case.read.</summary>
    private static FunctionDeclaration CaseScreen(string code, string subDomain, string processArea, string routeKey, string? feature) =>
        new(code, FunctionKind.Screen, subDomain, processArea, null, [Audiences.Staff], routeKey, feature, false, [], [CasePermissions.Read]);
}
