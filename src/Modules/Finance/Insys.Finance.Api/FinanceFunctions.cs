/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
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

namespace Insys.Finance.Api;

/// <summary>
/// The Finance module's functions (ADR-026): each screen with the read operations it covers, each action with its
/// operation, by endpoint name, in the sub-domains of docs/specs/INSYS_NAVIGATION.md. Portal screens are declared once
/// per portal. The function catalogue is generated from these.
/// </summary>
internal static class FinanceFunctions
{
    public static readonly IReadOnlyList<FunctionDeclaration> All =
    [
        .. new FunctionMap([Audiences.Staff], null)
        .Screen("FINANCE_GL_EXPORTS", "FINANCE", "GL export", "finance/gl-exports", "FinanceListGlExports", "FinanceGetGlExport", "FinanceGetGlExportContent")
        .Action("FINANCE_EXPORT_TO_GENERAL_LEDGER", "FINANCE_GL_EXPORTS", "FinanceExportToGeneralLedger")
        .Action("FINANCE_ACKNOWLEDGE_GL_EXPORT", "FINANCE_GL_EXPORTS", "FinanceAcknowledgeGlExport")
        .Screen("FINANCE_JOURNALS", "FINANCE", "Journals", "finance/journals", "FinanceGetJournal", "FinanceListJournals")
        .Action("FINANCE_REVERSE_JOURNAL", "FINANCE_JOURNALS", "FinanceReverseJournal")
        .Screen("FINANCE_PERIODS", "FINANCE", "Periods & close", "finance/periods", "FinanceListPeriods", "FinanceTrialBalance")
        .Action("FINANCE_PERIOD_OPEN", "FINANCE_PERIODS", "FinancePeriodOpen")
        .Action("FINANCE_PERIOD_SOFT_CLOSE", "FINANCE_PERIODS", "FinancePeriodSoftClose")
        .Action("FINANCE_PERIOD_CLOSE", "FINANCE_PERIODS", "FinancePeriodClose")
        .Action("FINANCE_PERIOD_REQUEST_REOPEN", "FINANCE_PERIODS", "FinancePeriodRequestReopen")
        .Screen("FINANCE_POSTING_EXCEPTIONS", "FINANCE", "Posting exceptions", "finance/posting-exceptions", "FinanceListPostingExceptions")
        .Action("FINANCE_RETRY_POSTING", "FINANCE_POSTING_EXCEPTIONS", "FinanceRetryPosting")
            .Functions,
    ];
}
