/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
namespace Insys.Health.Application;

/// <summary>
/// The static data tables Health's code fields take their codes from (BR-072), as schema.table: the OpenAPI documents
/// name them on each field as x-code-list, so a client offers the codes in force and their labels.
/// </summary>
public static class HealthCodeLists
{
    public const string PlansAndOptions = "health.cfg_plans_and_options";
    public const string DependantTypes = "health.cfg_dependant_types_and_age_limits";
    public const string ServiceTypes = "health.cfg_authorisation_validity_periods";
    public const string Sponsors = "health.mst_sponsor_register";
    public const string SchemeCategories = "health.cfg_scheme_member_categories";
    public const string CollectionMethods = "billing.ref_collection_methods";
    public const string Diagnoses = "health.ref_diagnosis_codes";
}
