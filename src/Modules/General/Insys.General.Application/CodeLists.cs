/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : General
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
namespace Insys.General.Application;

/// <summary>
/// The static data tables General's code fields take their codes from (BR-036), as schema.table: the OpenAPI documents
/// name them on each field as x-code-list, so a client offers the codes in force and their labels.
/// </summary>
public static class CodeLists
{
    public const string BillingFrequencies = "billing.ref_billing_frequencies";
    public const string CollectionMethods = "billing.ref_collection_methods";
    public const string EndorsementTypes = "general.cfg_endorsement_types";
    public const string RiskObjectTypes = "general.cfg_risk_object_types_and_attribute_schemas";
    public const string ValuationBases = "general.ref_valuation_bases";
    public const string ClaimPartyRoles = "general.cfg_claim_party_roles";
    public const string RiskZones = "general.ref_risk_zones";
    public const string LossCauses = "general.ref_loss_cause_codes";
    public const string Perils = "general.ref_peril_codes";
    public const string CatastropheEvents = "general.mst_catastrophe_event_register";
    public const string RecoveryTypes = "general.ref_recovery_types";
    public const string NonRenewalReasons = "general.cfg_non_renewal_reasons";
    public const string RetentionOffers = "general.cfg_retention_offers";
    public const string DisposalMethods = "general.ref_salvage_disposal_methods";
    public const string CancellationReasons = "general.cfg_cancellation_reasons_and_refund_rules";
    public const string ReferralReasons = "general.ref_referral_reason_codes";
    public const string UnderwritingDecisions = "general.ref_underwriting_decision_codes";
    public const string ReserveTypes = "claims.ref_reserve_types";
    public const string ClaimDecisionReasons = "claims.ref_claim_decision_reason_codes";
}
