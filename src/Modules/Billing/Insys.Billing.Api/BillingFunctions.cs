/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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

namespace Insys.Billing.Api;

/// <summary>
/// The Billing module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class BillingFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("BILLING_ACCOUNTS", "BILLING", "Accounts", "billing/accounts", "BillingGetAccount")
        .Action("BILLING_OPEN_ACCOUNT", "BILLING_ACCOUNTS", "BillingOpenAccount")
        .Action("BILLING_CREATE_SCHEDULE", "BILLING_ACCOUNTS", "BillingCreateSchedule")
        .Action("BILLING_AMEND_SCHEDULE", "BILLING_ACCOUNTS", "BillingAmendSchedule")
        .Action("BILLING_ISSUE_INVOICES", "BILLING_ACCOUNTS", "BillingIssueInvoices")
        .Action("BILLING_AGE_ACCOUNT", "BILLING_ACCOUNTS", "BillingAgeAccount")
        .Action("BILLING_CLOSE_ACCOUNT", "BILLING_ACCOUNTS", "BillingCloseAccount")
        .Action("BILLING_REQUEST_REFUND", "BILLING_ACCOUNTS", "BillingRequestRefund")
        .Screen("BILLING_INVOICES", "BILLING", "Invoices & credit notes", "billing/invoices", "BillingGetInvoice", "BillingListInvoices")
        .Action("BILLING_RAISE_CREDIT_NOTE", "BILLING_INVOICES", "BillingRaiseCreditNote")
        .Screen("BILLING_DASHBOARD", "BILLING", "Arrears ageing & cases", "billing/summary", "BillingSummary")
        .Screen("BILLING_REFUNDS", "BILLING", "Refunds (second person)", "billing/refunds", "BillingGetRefund")
        .Job("BILLING_DAILY_JOBS", "BILLING", "Background processing")
            .Functions,
        .. new FunctionMap([Audiences.Customer], Features.CustomerPortal)
        .Screen("CUSTOMER_PORTAL_BILLING_INVOICES", "CUSTOMER_PORTAL", "Invoices", "portal/billing/invoices", "PortalBillingListInvoices", "PortalBillingGetInvoice")
            .Functions,
        .. new FunctionMap([Audiences.Broker], Features.BrokerPortal)
        .Screen("BROKER_PORTAL_BILLING_INVOICES", "BROKER_PORTAL", "Invoices", "portal/billing/invoices", "PortalBillingListInvoices", "PortalBillingGetInvoice")
            .Functions,
    ];
}
