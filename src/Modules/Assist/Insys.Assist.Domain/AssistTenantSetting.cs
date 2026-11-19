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

    /// <summary>AI-3 (7.8): one pass at a time on a setup.</summary>
    [ErrorCode(409, "error.assist.setup_pass_open", Means = "An AI pass is already queued or running on this setup, and a setup takes one pass at a time.",
        Remedy = "Wait for the pass to finish, then read the setup again and ask for another if you need one.")]
    public const string SetupPassOpen = "ASSIST_SETUP_PASS_OPEN";

    /// <summary>AI-3 (7.8): the setup was submitted, or its answers changed, while the pass waited or ran, so nothing it found was recorded.</summary>
    [ErrorCode(409, "error.assist.setup_changed", Means = "The setup changed while the AI pass ran: its answers changed or it was submitted, so nothing the pass found was recorded in it.",
        Remedy = "Read the setup again and ask for a new pass if it is still a draft.")]
    public const string SetupChanged = "ASSIST_SETUP_CHANGED";

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
    [ErrorCode(422, "error.assist.document_not_allowed", Means = "A document isn't one AI-1 reads: a run takes 1 to 10 different documents, each a PDF, DOCX or XLSX file of at most 20 MB, filed under a category that holds no personal data.",
        Remedy = "Check the documents against those limits, then start the run again with documents that meet them.")]
    public const string DocumentNotAllowed = "ASSIST_DOCUMENT_NOT_ALLOWED";

    /// <summary>AI-1 (D-16): the uploader hasn't confirmed that a document holds no customer personal data.</summary>
    [ErrorCode(422, "error.assist.confirmation_required", Means = "Each document needs your confirmation that it holds no customer personal data before AI-1 reads it.",
        Remedy = "Confirm each document, then start the run again.")]
    public const string ConfirmationRequired = "ASSIST_CONFIRMATION_REQUIRED";

    /// <summary>AI-1 (D-16): a document's content changed after it was confirmed, so it needs a new confirmation in a new run.</summary>
    [ErrorCode(409, "error.assist.confirmation_stale", Means = "A document's content changed after you confirmed it, so the run didn't read it.",
        Remedy = "Check the document again, then start a new run and confirm it again.")]
    public const string ConfirmationStale = "ASSIST_CONFIRMATION_STALE";

    /// <summary>AI-1 (D-16, §5.5): only the user who uploaded a document confirms it; another user's upload is refused, and is uploaded again to be used.</summary>
    [ErrorCode(403, "error.assist.not_uploader", Means = "Only the user who uploaded a document can confirm it holds no customer personal data, and someone else uploaded this one.",
        Remedy = "Upload the document again yourself, then start the run with your copy.")]
    public const string NotUploader = "ASSIST_NOT_UPLOADER";

    /// <summary>AI-1 (D-16, §5.5): the identifier check found an obvious identifier, named by kind and page in the run, never by value.</summary>
    [ErrorCode(422, "error.assist.document_identifiers", Means = "A document holds an obvious identifier, such as an email address, an ID number or a bank account number, so AI-1 refused it and sent nothing. The run lists the kind of identifier and the page.",
        Remedy = "Remove the identifiers from the document, upload the corrected copy, then start a new run.")]
    public const string DocumentIdentifiers = "ASSIST_DOCUMENT_IDENTIFIERS";

    /// <summary>AI-1 (AIA-011): a document couldn't be read at all, or none of its pages has a text layer.</summary>
    [ErrorCode(422, "error.assist.source_unreadable", Means = "A document couldn't be read: the file couldn't be opened, or none of its pages has text, such as a scanned image.",
        Remedy = "Upload a copy with a text layer, for example the original file rather than a scan, then start a new run.")]
    public const string SourceUnreadable = "ASSIST_SOURCE_UNREADABLE";

    /// <summary>AI-1 (AIA-017): the user the run acts for may no longer act (disabled, or their roles changed), so it stopped without writing.</summary>
    [ErrorCode(403, "error.assist.user_cannot_act", Means = "The run stopped without writing anything, because you can no longer do what it does for you: your account or your roles changed after you started it.",
        Remedy = "Ask your administrator to check your access, then start a new run.")]
    public const string UserCannotAct = "ASSIST_USER_CANNOT_ACT";

    /// <summary>AI-1: a run stopped part way, its job interrupted; it isn't resumed, so a new run is started.</summary>
    [ErrorCode(409, "error.assist.run_stalled", Means = "The run was interrupted part way and isn't resumed.",
        Remedy = "Start a new run. If the run created a Draft version, finish that one first: a product has one version in progress at a time.")]
    public const string RunStalled = "ASSIST_RUN_STALLED";

    /// <summary>AI-1: a document, or the run's documents together, hold more than AI-1 reads (an expanded package, a sheet, or the text over its limit).</summary>
    [ErrorCode(422, "error.assist.source_too_large", Means = "The documents hold more than AI-1 reads in one run: a file that expands too far, a sheet that is too large, or too much text in total.",
        Remedy = "Split the material into smaller files, or start runs with fewer documents.")]
    public const string SourceTooLarge = "ASSIST_SOURCE_TOO_LARGE";

    /// <summary>AI-1: a run waited too long to start, so its confirmations are no longer relied on; a new run is started.</summary>
    [ErrorCode(409, "error.assist.run_expired", Means = "The run waited more than an hour to start, so its confirmations are no longer relied on.",
        Remedy = "Start a new run and confirm the documents again.")]
    public const string RunExpired = "ASSIST_RUN_EXPIRED";

    /// <summary>AIA-050: the provider said inference ran outside the tenant's region, so nothing it returned is used.</summary>
    [ErrorCode(422, "error.assist.residency_mismatch", Means = "The AI provider said it answered from outside your tenant's inference region, so nothing it returned was used.",
        Remedy = "Start a new run later. If it happens again, ask your administrator to check the AI Assist settings.")]
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
