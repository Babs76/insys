/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.BuildingBlocks.Domain;

namespace Insys.Assist.Domain;

public static class AssistErrorCodes
{
    /// <summary>The model can't be reached; the screen carries on without it (P8).</summary>
    [ErrorCode(503, "error.assist.unavailable", true)]
    public const string Unavailable = "ASSIST_UNAVAILABLE";

    /// <summary>The tenant's monthly cap is reached (AIA-073).</summary>
    [ErrorCode(429, "error.assist.cap_reached")]
    public const string CapReached = "ASSIST_CAP_REACHED";

    /// <summary>Inference runs outside the tenant's jurisdiction with no legal basis recorded, or the setting isn't made (AIA-050).</summary>
    [ErrorCode(422, "error.assist.residency_not_set")]
    public const string ResidencyNotSet = "ASSIST_RESIDENCY_NOT_SET";

    /// <summary>The verifier rejected the model's output, so the caller gets the plain data instead (AIA-033).</summary>
    [ErrorCode(422, "error.assist.output_rejected")]
    public const string OutputRejected = "ASSIST_OUTPUT_REJECTED";
}

/// <summary>
/// A tenant's AI Assist settings (AIA-050, SRS §7.2): its jurisdiction, where inference runs and the legal basis it relies on. One per tenant.
/// Nothing assumes South African hosting. The monthly cap is the operator's (AIA-073), so it isn't changed here.
/// </summary>
public sealed class AssistTenantSetting : AggregateRoot<Guid>
{
    private AssistTenantSetting()
    {
    }

    /// <summary>The tenant's jurisdiction, as a country code.</summary>
    public string Jurisdiction { get; private set; } = "";

    /// <summary>The country where inference runs, or GLOBAL.</summary>
    public string InferenceRegion { get; private set; } = "";

    public string? LegalBasis { get; private set; }

    public bool ExplanationsEnabled { get; private set; }

    /// <summary>The tokens the tenant may use in a calendar month; null means none until the operator sets it.</summary>
    public long? MonthlyTokenCap { get; private set; }

    public static AssistTenantSetting Start() => new() { Id = Guid.CreateVersion7() };

    public void Change(string jurisdiction, string inferenceRegion, string? legalBasis, bool explanationsEnabled)
    {
        Jurisdiction = jurisdiction;
        InferenceRegion = inferenceRegion;
        LegalBasis = string.IsNullOrWhiteSpace(legalBasis) ? null : legalBasis.Trim();
        ExplanationsEnabled = explanationsEnabled;
        MarkChanged();
    }

    /// <summary>AIA-050: inference in the tenant's jurisdiction needs no more; anywhere else needs a legal basis recorded.</summary>
    public bool ResidencyAllowsInference => Jurisdiction.Length > 0 && (InferenceRegion == Jurisdiction || LegalBasis is not null);
}
