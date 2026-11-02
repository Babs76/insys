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
using System.Collections.Concurrent;
using Insys.Assist.Application;

namespace Insys.Assist.Infrastructure;

/// <summary>
/// The test stub, and in batch 7.1 the only provider (SRS §9.2: no real model in CI). It gives fixed outputs, including wrong numbers and an injected instruction's effect, and
/// keeps what it was sent, so a test can read the payload. It reads and writes nothing else.
/// </summary>
public sealed class StubAiModelProvider : IAiModelProvider
{
    private readonly ConcurrentQueue<AiModelRequest> _sent = new();

    /// <summary>What it answers with; by default a fixed sentence with no numbers.</summary>
    public Func<AiModelRequest, string> Reply { get; set; } = _ => "This is a fixed explanation from the test stub.";

    public bool Unavailable { get; set; }

    public IReadOnlyCollection<AiModelRequest> Sent => _sent;

    public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new AiModelUnavailableException("The stub is set to be unavailable.");
        }

        _sent.Enqueue(request);
        var text = Reply(request);
        return Task.FromResult(new AiModelResponse(text, (request.Instructions.Length + request.Data.Length) / 4, text.Length / 4));
    }
}
