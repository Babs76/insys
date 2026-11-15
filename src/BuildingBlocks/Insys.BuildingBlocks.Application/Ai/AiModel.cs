/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Application
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
namespace Insys.BuildingBlocks.Application.Ai;

/// <param name="ModelVersion">The model to ask, from configuration (AIA-074).</param>
/// <param name="PromptVersion">The prompt template's version, from configuration (AIA-074).</param>
/// <param name="Instructions">The prompt template's text: INSYS's own words, never record data (AIA-061).</param>
/// <param name="Data">The minimised data, delimited: never instructions (AIA-061).</param>
public sealed record AiModelRequest(string ModelVersion, string PromptVersion, string Instructions, string Data);

/// <param name="ServedModel">The model the provider says answered, when it says.</param>
/// <param name="InferenceRegion">Where the provider says inference ran, as it reports it, never assumed (AIA-050); null when it doesn't say.</param>
public sealed record AiModelResponse(string Text, long InputTokens, long OutputTokens, string? ServedModel = null, string? InferenceRegion = null);

/// <summary>
/// The one interface for model calls (AI Assist SRS §7.1). It sends text and receives text: it has no tools, so the model can't write, approve, pay or send
/// (AIA-060). The test stub is in Insys.Testing; the real provider is in the Cloud adapter project, behind ADR-027's SDK guard.
/// </summary>
public interface IAiModelProvider
{
    Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What runs where no model is configured: no model at all, so every run ends UNAVAILABLE and INSYS carries on (P8).
/// </summary>
public sealed class NoAiModelProvider : IAiModelProvider
{
    public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken) =>
        throw new AiModelUnavailableException("No AI model is connected to this deployment.");
}

/// <summary>
/// A provider's failure: the caller gets ASSIST_UNAVAILABLE, and INSYS carries on (P8). It never carries record data or the provider's own error, whose text can
/// echo the request (AIA-051); only what the call used, when the provider says, so a refusal or an answer cut short is still metered (AIA-073).
/// </summary>
public sealed class AiModelUnavailableException(string message, long inputTokens = 0, long outputTokens = 0) : Exception(message)
{
    public long InputTokens { get; } = inputTokens;

    public long OutputTokens { get; } = outputTokens;
}
