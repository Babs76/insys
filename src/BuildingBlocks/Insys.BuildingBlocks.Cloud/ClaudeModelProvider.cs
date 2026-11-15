/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Cloud
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
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Insys.BuildingBlocks.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Insys.BuildingBlocks.Cloud;

/// <summary>Assist:Claude: the Claude API route (ai-assist.md D-1). The key comes from the platform's secret store, never tenant configuration or logs (AIA-053).</summary>
public sealed class ClaudeModelOptions
{
    /// <summary>The API key: from the deployment's secret store (Assist__Claude__ApiKey), never written to a file or a log.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Another address for the API, for a deployment that reaches it through a gateway; empty for Anthropic's own.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The most a call may use, its reasoning included (the model always reasons before it answers); AI-2's templates ask for at most 150 words.</summary>
    public int MaxTokens { get; set; } = 8192;

    /// <summary>How much the model reasons before answering: low, medium, high, xhigh or max; empty for the model's own default.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Where inference must run, when the deployment sets it (the API's inference_geo); empty to leave it to the route. What actually ran is recorded from the answer.</summary>
    public string InferenceGeo { get; set; } = "";

    /// <summary>How long one call may take, with no retry: AI-2 answers in 10 seconds at the 95th percentile (AIA-070), and INSYS never waits on the AI (P8).</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// The Claude implementation of <see cref="IAiModelProvider"/> (ai-assist.md §5, D-1), through Anthropic's official SDK, which only this project may reference
/// (ADR-027's SDK guard). One message, text in and text out: no tools, so the model can't write, approve, pay or send (AIA-060). The template is the system
/// prompt and the delimited data the one user message (AIA-061). Any failure, a refusal or an answer cut short is unavailable, so INSYS shows the plain
/// breakdown (P8); nothing of the request is logged or put in the exception (AIA-051, AIA-053).
/// </summary>
public sealed class ClaudeModelProvider : IAiModelProvider
{
    private readonly AnthropicClient _client;
    private readonly ClaudeModelOptions _options;
    private readonly ILogger<ClaudeModelProvider> _logger;

    public ClaudeModelProvider(IOptions<ClaudeModelOptions> options, ILogger<ClaudeModelProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Assist:Provider \"claude\" needs Assist:Claude:ApiKey, from the deployment's secret store.");
        }

        _client = _options.BaseUrl.Length > 0
            ? new AnthropicClient { ApiKey = _options.ApiKey, BaseUrl = _options.BaseUrl, Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds), MaxRetries = 0 }
            : new AnthropicClient { ApiKey = _options.ApiKey, Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds), MaxRetries = 0 };
    }

    public async Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.ModelVersion,
            MaxTokens = _options.MaxTokens,
            System = request.Instructions,
            Messages = [new UserMessageParam { Content = request.Data }],
        };
        if (_options.Effort.Length > 0)
        {
            parameters = parameters with { OutputConfig = new OutputConfig { Effort = _options.Effort } };
        }

        if (_options.InferenceGeo.Length > 0)
        {
            parameters = parameters with { InferenceGeo = _options.InferenceGeo };
        }

        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            // The status only: the request and the answer stay out of logs (AIA-051).
            _logger.LogWarning("The Claude API refused or failed a call: {Status}.", ex.StatusCode);
            throw new AiModelUnavailableException("The AI model is unavailable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The Claude API could not be reached: {Error}.", ex.GetType().Name);
            throw new AiModelUnavailableException("The AI model is unavailable.");
        }

        if (message.StopReason is not { } stop || stop != StopReason.EndTurn)
        {
            // A refusal, or an answer cut short at the token limit: nothing partial is shown (P8).
            _logger.LogWarning("The Claude API ended a call without a complete answer: {StopReason}.", message.StopReason?.ToString() ?? "none");
            throw new AiModelUnavailableException("The AI model gave no complete answer.", message.Usage.InputTokens, message.Usage.OutputTokens);
        }

        var text = string.Concat(message.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (text.Length == 0)
        {
            throw new AiModelUnavailableException("The AI model gave no text.", message.Usage.InputTokens, message.Usage.OutputTokens);
        }

        return new AiModelResponse(text, message.Usage.InputTokens, message.Usage.OutputTokens, message.Model, message.Usage.InferenceGeo);
    }
}
