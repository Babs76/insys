/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.Integration.Application.DeadLetters;

namespace Insys.Integration.Infrastructure;

/// <summary>Receives messages the outbox relay gave up on and records them as DeadLetter aggregates.</summary>
internal sealed class DeadLetterSink(IDispatcher dispatcher) : IDeadLetterSink
{
    public async Task DeadLetter(EventEnvelope envelope, string source, string error, short attempts, DateTimeOffset firstFailedAt, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Send(new DeadLetterMessage(source, envelope.EventId.ToString(),
            JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web), error, firstFailedAt, attempts), cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Dead-lettering event {envelope.EventId} failed with {result.Error.Code}.");
        }
    }
}
