/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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

namespace Insys.Payments.Api;

/// <summary>
/// The Payments module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class PaymentsFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("PAYMENT_CALLBACK_ENDPOINTS", "PAYMENTS", "Provider callbacks", "payments/callback-endpoints", "PaymentsListCallbackEndpoints")
        .Action("PAYMENTS_ISSUE_CALLBACK_ENDPOINT", "PAYMENT_CALLBACK_ENDPOINTS", "PaymentsIssueCallbackEndpoint", privileged: true)
        .Action("PAYMENTS_REVOKE_CALLBACK_ENDPOINT", "PAYMENT_CALLBACK_ENDPOINTS", "PaymentsRevokeCallbackEndpoint", privileged: true)
        .Screen("PAYMENTS", "PAYMENTS", "Collections (debit order)", "payments/payments", "PaymentsList", "PaymentsGet")
        .Action("PAYMENTS_INITIATE", "PAYMENTS", "PaymentsInitiate")
        .Action("PAYMENTS_RESOLVE_UNKNOWN", "PAYMENTS", "PaymentsResolveUnknown")
        .Action("PAYMENTS_REVERSE", "PAYMENTS", "PaymentsReverse", privileged: true)
        .Action("PAYMENTS_ALLOCATE", "PAYMENTS", "PaymentsAllocate")
        .Screen("PAYMENT_PAYOUT_BATCHES", "PAYMENTS", "Payout batches (release approval)", "payments/payout-batches", "PaymentsGetBatch", "PaymentsListBatchPayments")
        .Action("PAYMENTS_BUILD_BATCH", "PAYMENT_PAYOUT_BATCHES", "PaymentsBuildBatch")
        .Action("PAYMENTS_RELEASE_BATCH", "PAYMENT_PAYOUT_BATCHES", "PaymentsReleaseBatch")
        .Screen("PAYMENT_RECONCILIATION", "PAYMENTS", "Bank statement reconciliation", "payments/reconciliation-exceptions", "PaymentsListExceptions")
        .Action("PAYMENTS_ASSIGN_EXCEPTION", "PAYMENT_RECONCILIATION", "PaymentsAssignException")
        .Action("PAYMENTS_RESOLVE_EXCEPTION", "PAYMENT_RECONCILIATION", "PaymentsResolveException")
        .Screen("PAYMENT_DASHBOARD", "PAYMENTS", "Collections (debit order)", "payments/summary", "PaymentsSummary")
        .Action("PAYMENTS_IMPORT_STATEMENT", "PAYMENT_RECONCILIATION", "PaymentsImportStatement")
        .Permission("COMMISSION_PAYOUT_VIEW", "PAYMENTS", "commission_payout.read")
        .Job("PAYMENTS_DAILY_JOBS", "PAYMENTS", "Background processing")
            .Functions,
    ];
}
