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
    [ErrorCode(422, "error.assist.cap_reached")]
    public const string CapReached = "ASSIST_CAP_REACHED";

    /// <summary>Inference runs outside the tenant's jurisdiction with no legal basis recorded, or the setting isn't made (AIA-050).</summary>
    [ErrorCode(422, "error.assist.residency_not_set")]
    public const string ResidencyNotSet = "ASSIST_RESIDENCY_NOT_SET";

    /// <summary>The tenant's inference region isn't one the AI provider can be asked to run in (AIA-050): a setting made before the regions were the provider's.</summary>
    [ErrorCode(422, "error.assist.region_not_supported", Means = "The region your settings name for AI inference isn't one the AI provider can run in, so no explanation is asked for.",
        Remedy = "Ask your administrator to choose one of the regions offered in the AI Assist settings, with the legal basis for it.")]
    public const string RegionNotSupported = "ASSIST_REGION_NOT_SUPPORTED";

    /// <summary>The tenant's administrator hasn't switched this feature on.</summary>
    [ErrorCode(422, "error.assist.not_enabled")]
    public const string NotEnabled = "ASSIST_NOT_ENABLED";

    /// <summary>The verifier rejected the model's output, so the caller gets the plain data instead (AIA-033).</summary>
    [ErrorCode(422, "error.assist.output_rejected")]
    public const string OutputRejected = "ASSIST_OUTPUT_REJECTED";

    /// <summary>AI-1 (ai-assist.md §5.5): a document isn't a PDF, DOCX or XLSX by its content, is over 20 MB, or the run names more than 10.</summary>
    [ErrorCode(422, "error.assist.document_not_allowed")]
    public const string DocumentNotAllowed = "ASSIST_DOCUMENT_NOT_ALLOWED";

    /// <summary>AI-1 (D-16): the uploader hasn't confirmed that a document holds no customer personal data.</summary>
    [ErrorCode(422, "error.assist.confirmation_required")]
    public const string ConfirmationRequired = "ASSIST_CONFIRMATION_REQUIRED";

    /// <summary>AI-1 (D-16): a document's content changed after it was confirmed, so it needs a new confirmation in a new run.</summary>
    [ErrorCode(409, "error.assist.confirmation_stale")]
    public const string ConfirmationStale = "ASSIST_CONFIRMATION_STALE";

    /// <summary>AI-1 (D-16, §5.5): the identifier check found an obvious identifier, named by kind and page in the run, never by value.</summary>
    [ErrorCode(422, "error.assist.document_identifiers")]
    public const string DocumentIdentifiers = "ASSIST_DOCUMENT_IDENTIFIERS";

    /// <summary>AI-1 (AIA-011): a document couldn't be read at all, or none of its pages has a text layer.</summary>
    [ErrorCode(422, "error.assist.source_unreadable")]
    public const string SourceUnreadable = "ASSIST_SOURCE_UNREADABLE";

    /// <summary>AI-1 (AIA-017): the user the run acts for may no longer act (disabled, or their roles changed), so it stopped without writing.</summary>
    [ErrorCode(403, "error.assist.user_cannot_act")]
    public const string UserCannotAct = "ASSIST_USER_CANNOT_ACT";

    /// <summary>AI-1: a run stopped part way, its job interrupted; it isn't resumed, so a new run is started.</summary>
    [ErrorCode(409, "error.assist.run_stalled")]
    public const string RunStalled = "ASSIST_RUN_STALLED";

    /// <summary>AI-1: a document, or the run's documents together, hold more than AI-1 reads (an expanded package, a sheet, or the text over its limit).</summary>
    [ErrorCode(422, "error.assist.source_too_large")]
    public const string SourceTooLarge = "ASSIST_SOURCE_TOO_LARGE";

    /// <summary>AI-1: a run waited too long to start, so its confirmations are no longer relied on; a new run is started.</summary>
    [ErrorCode(409, "error.assist.run_expired")]
    public const string RunExpired = "ASSIST_RUN_EXPIRED";

    /// <summary>AIA-050: the provider said inference ran outside the tenant's region, so nothing it returned is used.</summary>
    [ErrorCode(422, "error.assist.residency_mismatch")]
    public const string ResidencyMismatch = "ASSIST_RESIDENCY_MISMATCH";
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

    /// <summary>Where the tenant allows inference to run, in the AI provider's terms (Assist:InferenceRegions: US or GLOBAL today). A setting made earlier may hold a country code, which no call can satisfy.</summary>
    public string InferenceRegion { get; private set; } = "";

    public string? LegalBasis { get; private set; }

    public bool ExplanationsEnabled { get; private set; }

    /// <summary>The tokens the tenant may use in a calendar month; null means the deployment's low default (Assist:DefaultMonthlyTokenCap).</summary>
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
