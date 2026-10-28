/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

namespace Insys.ClaimsFramework.Api;

/// <summary>
/// The ClaimsFramework module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class ClaimsFrameworkFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("CLAIM_REGISTER", "CLAIM_REGISTER", "Registration from all domains", "claims/claims", "ClaimsSearchRegister", "ClaimsGet", "ClaimsListCommunications")
        .Action("CLAIMS_ALLOCATE_NUMBER", "CLAIM_REGISTER", "ClaimsAllocateNumber")
        .Action("CLAIMS_REQUEST_EVIDENCE", "CLAIM_REGISTER", "ClaimsRequestEvidence")
        .Action("CLAIMS_RECORD_EVIDENCE", "CLAIM_REGISTER", "ClaimsRecordEvidence")
        .Action("CLAIMS_RECORD_COMMUNICATION", "CLAIM_REGISTER", "ClaimsRecordCommunication")
        .Action("CLAIMS_EVALUATE_FRAUD", "CLAIM_REGISTER", "ClaimsEvaluateFraud")
        .Screen("CLAIM_FINANCIALS", "CLAIM_FINANCIALS", "Reserves", "claims/financials", "ClaimsGetFinancials")
        .Action("CLAIMS_MOVE_RESERVE", "CLAIM_FINANCIALS", "ClaimsMoveReserve")
        .Screen("CLAIM_PAYMENT_INSTRUCTIONS", "CLAIM_FINANCIALS", "Payment instructions", "claims/payment-instructions", "ClaimsGetInstruction", "ClaimsListInstructions")
        .Action("CLAIMS_DRAFT_INSTRUCTION", "CLAIM_PAYMENT_INSTRUCTIONS", "ClaimsDraftInstruction")
        .Action("CLAIMS_SUBMIT_INSTRUCTION", "CLAIM_PAYMENT_INSTRUCTIONS", "ClaimsSubmitInstruction")
        .Action("CLAIMS_RELEASE_INSTRUCTION", "CLAIM_PAYMENT_INSTRUCTIONS", "ClaimsReleaseInstruction")
        .Action("CLAIMS_RETRY_INSTRUCTION", "CLAIM_PAYMENT_INSTRUCTIONS", "ClaimsRetryInstruction")
        .Action("CLAIMS_REDIRECT_INSTRUCTION", "CLAIM_PAYMENT_INSTRUCTIONS", "ClaimsRedirectInstruction")
            .Functions,
    ];
}
