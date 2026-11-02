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

public static class AssistFeatures
{
    public const string ExplainRefusal = "EXPLAIN_REFUSAL";
    public const string ExplainPremium = "EXPLAIN_PREMIUM";
    public const string ProductDraft = "PRODUCT_DRAFT";
    public const string TenantSetup = "TENANT_SETUP";
}

public static class AssistRunStatuses
{
    /// <summary>The output passed the verifier.</summary>
    public const string Verified = "VERIFIED";

    /// <summary>The verifier rejected the output, which isn't shown (AIA-033).</summary>
    public const string Rejected = "REJECTED";

    public const string Unavailable = "UNAVAILABLE";
}

/// <summary>One model call and how it ended (SRS §7.2): metered from its tokens (AIA-073). Written once, when it ends.</summary>
public sealed class AssistRun : AggregateRoot<Guid>
{
    private AssistRun()
    {
    }

    public string Feature { get; private set; } = "";

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset FinishedAt { get; private set; }

    public string ModelVersion { get; private set; } = "";

    public string PromptVersion { get; private set; } = "";

    public string Status { get; private set; } = "";

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>Filled from the price list from batch 7.3 (D-5); the stub costs nothing.</summary>
    public decimal? Cost { get; private set; }

    public string? CostCurrency { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public static AssistRun Finish(string feature, DateTimeOffset startedAt, DateTimeOffset finishedAt, string modelVersion, string promptVersion, string status, long inputTokens,
        long outputTokens, Guid? correlationId) => new()
        {
            Id = Guid.CreateVersion7(), Feature = feature, StartedAt = startedAt, FinishedAt = finishedAt, ModelVersion = modelVersion, PromptVersion = promptVersion, Status = status,
            InputTokens = inputTokens, OutputTokens = outputTokens, CorrelationId = correlationId,
        };
}

/// <summary>The minimised payload actually sent, and the raw output, even when rejected (AIA-051). Never logged.</summary>
public sealed class AssistRunPayload : AggregateRoot<Guid>
{
    private AssistRunPayload()
    {
    }

    public Guid RunId { get; private set; }

    public string PayloadSent { get; private set; } = "";

    public string? RawOutput { get; private set; }

    public string Masked { get; private set; } = "{}";

    public static AssistRunPayload For(Guid runId, string payloadSent, string? rawOutput, string masked) => new()
    {
        Id = Guid.CreateVersion7(), RunId = runId, PayloadSent = payloadSent, RawOutput = rawOutput, Masked = masked,
    };
}
