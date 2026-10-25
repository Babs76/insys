/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Compliance
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

namespace Insys.Compliance.Api;

/// <summary>
/// The Compliance module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class ComplianceFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("COMPLIANCE_REPORT_RUNS", "REGULATORY_REPORTING", "Report runs", "compliance/report-runs", "ComplianceListReportRuns", "ComplianceDownloadReportExport", "ComplianceGetReportRun")
        .Action("COMPLIANCE_RUN_REPORT", "COMPLIANCE_REPORT_RUNS", "ComplianceRunReport")
        .Screen("COMPLIANCE_RETURNS", "REGULATORY_REPORTING", "Returns (multi-person)", "compliance/returns", "ComplianceListReturns", "ComplianceReturnsSummary", "ComplianceGetReturn")
        .Action("COMPLIANCE_PREPARE_RETURN", "COMPLIANCE_RETURNS", "CompliancePrepareReturn")
        .Action("COMPLIANCE_REVIEW_RETURN", "COMPLIANCE_RETURNS", "ComplianceReviewReturn")
        .Action("COMPLIANCE_APPROVE_RETURN", "COMPLIANCE_RETURNS", "ComplianceApproveReturn")
        .Action("COMPLIANCE_SUBMIT_RETURN", "COMPLIANCE_RETURNS", "ComplianceSubmitReturn")
        .Action("COMPLIANCE_ACCEPT_RETURN", "COMPLIANCE_RETURNS", "ComplianceAcceptReturn")
        .Action("COMPLIANCE_REJECT_RETURN", "COMPLIANCE_RETURNS", "ComplianceRejectReturn")
        .Action("COMPLIANCE_CORRECT_RETURN", "COMPLIANCE_RETURNS", "ComplianceCorrectReturn")
            .Functions,
    ];
}
