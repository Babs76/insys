/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications
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
using System.Globalization;

namespace Insys.Communications.Application.Triggers;

/// <summary>What a triggered notification is about (its source_type, addenda/communications.md §1, §6).</summary>
public static class TriggerSources
{
    public const string BillingAccount = "BILLING_ACCOUNT";
    public const string GeneralPolicy = "GENERAL_POLICY";
    public const string GeneralClaim = "GENERAL_CLAIM";
    public const string LifeClaim = "LIFE_CLAIM";
    public const string HealthClaim = "HEALTH_CLAIM";
    public const string HealthPreauth = "HEALTH_PREAUTH";
}

/// <summary>How merge values are written: invariant culture, so a template renders the same in every tenant's locale.</summary>
public static class TriggerFormats
{
    public static string Amount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
