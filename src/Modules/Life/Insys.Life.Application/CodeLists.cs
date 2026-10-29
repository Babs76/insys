/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Life
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
namespace Insys.Life.Application;

/// <summary>
/// The static data tables Life's code fields take their codes from (BR-066, BR-078), as schema.table: the OpenAPI
/// documents name them on each field as x-code-list, so a client offers the codes in force and their labels.
/// </summary>
public static class LifeCodeLists
{
    public const string ClaimTypes = "life.ref_life_claim_types";
    public const string CausesOfEvent = "life.ref_cause_of_event_codes";
    public const string AlterationTypes = "life.cfg_alteration_types";
    public const string LoanRates = "life.cfg_policy_loan_rates";
    public const string SchemeTypes = "life.ref_group_scheme_types";
    public const string UnderwritingDecisions = "life.ref_underwriting_decision_codes";
    public const string ReserveTypes = "claims.ref_reserve_types";
    public const string ClaimDecisionReasons = "claims.ref_claim_decision_reason_codes";
    public const string Frequencies = "billing.ref_billing_frequencies";
    public const string CollectionMethods = "billing.ref_collection_methods";
}
