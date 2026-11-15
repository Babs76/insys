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

public static class ExplanationKinds
{
    public const string Refusal = "REFUSAL";
    public const string Premium = "PREMIUM";
}

/// <summary>How an explanation request ended: explained, or the plain facts shown instead (AIA-033, P8).</summary>
public static class ExplanationOutcomes
{
    /// <summary>The model's text passed the verifier and is shown.</summary>
    public const string Explained = "EXPLAINED";

    /// <summary>The verifier rejected the text, so the plain facts are shown (AIA-033).</summary>
    public const string Rejected = "REJECTED";

    /// <summary>The model didn't answer, so the plain facts are shown (P8).</summary>
    public const string Unavailable = "UNAVAILABLE";

    /// <summary>The refusal's code has no catalogue wording yet, so no call was made and the plain facts are shown (AIA-031).</summary>
    public const string NoWording = "NO_WORDING";
}

/// <summary>
/// An explanation (SRS §7.2): what was explained, the facts it was built from, the outcome and the text shown. Written once. A later request with the same
/// facts and prompt reuses an explained one (AIA-036), so the cache key is a hash of exactly what was sent.
/// </summary>
public sealed class AssistExplanation : AggregateRoot<Guid>
{
    private AssistExplanation()
    {
    }

    public string Kind { get; private set; } = "";

    /// <summary>What it explains: the refusal's code, or the calculation's ID.</summary>
    public string SourceRef { get; private set; } = "";

    public string CacheKey { get; private set; } = "";

    public string PromptVersion { get; private set; } = "";

    public string ModelVersion { get; private set; } = "";

    public string Outcome { get; private set; } = "";

    /// <summary>The facts the explanation was built from, as JSON: what the console shows when the text isn't (AIA-033).</summary>
    public string Facts { get; private set; } = "[]";

    public string? Text { get; private set; }

    /// <summary>The model call, if one was made.</summary>
    public Guid? RunId { get; private set; }

    /// <summary>A stored explanation reused, instead of a new call (AIA-036).</summary>
    public Guid? ReusedFrom { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public static AssistExplanation Record(string kind, string sourceRef, string cacheKey, string promptVersion, string modelVersion, string outcome, string facts, string? text,
        Guid? runId, Guid? reusedFrom, Guid? correlationId) => new()
        {
            Id = Guid.CreateVersion7(), Kind = kind, SourceRef = sourceRef, CacheKey = cacheKey, PromptVersion = promptVersion, ModelVersion = modelVersion, Outcome = outcome,
            Facts = facts, Text = outcome == ExplanationOutcomes.Explained ? text : null, RunId = runId, ReusedFrom = reusedFrom, CorrelationId = correlationId,
        };
}

public static class FeedbackReasons
{
    public const string Wrong = "WRONG";
    public const string Unclear = "UNCLEAR";
    public const string Incomplete = "INCOMPLETE";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Wrong, Unclear, Incomplete, Other };
}

/// <summary>A user's mark on an explanation, helpful or not, with a reason (AIA-037), for evaluation. Added to, never changed.</summary>
public sealed class AssistExplanationFeedback : AggregateRoot<Guid>
{
    private AssistExplanationFeedback()
    {
    }

    public Guid ExplanationId { get; private set; }

    public bool Helpful { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>The user's own words, kept for the evaluation team only: never sent to a model (D-15).</summary>
    public string? Comment { get; private set; }

    public static Result<AssistExplanationFeedback> Give(Guid explanationId, bool helpful, string? reason, string? comment)
    {
        if (!helpful && reason is null)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An explanation marked unhelpful or wrong needs a reason: WRONG, UNCLEAR, INCOMPLETE or OTHER.");
        }

        if (reason is not null && !FeedbackReasons.All.Contains(reason))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The reason is WRONG, UNCLEAR, INCOMPLETE or OTHER.");
        }

        return comment is { Length: > 500 }
            ? Error.Validation(ErrorCodes.ValidationFailed, "The comment is at most 500 characters.")
            : new AssistExplanationFeedback { Id = Guid.CreateVersion7(), ExplanationId = explanationId, Helpful = helpful, Reason = helpful ? null : reason, Comment = comment };
    }
}
