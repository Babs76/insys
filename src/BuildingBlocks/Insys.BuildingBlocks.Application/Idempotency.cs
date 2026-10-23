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
namespace Insys.BuildingBlocks.Application;

/// <summary>The stored outcome of an idempotent request (INT-003, DBA-008).</summary>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, string? Body, IReadOnlyDictionary<string, string> Headers);

public enum IdempotencyBeginOutcome
{
    Started,
    Replay,
    KeyReusedWithDifferentRequest,
    InProgress,
}

public sealed record IdempotencyBeginResult(IdempotencyBeginOutcome Outcome, IdempotentResponse? Response = null);

/// <summary>Stores idempotency keys in integration.idempotency_key, scoped to the current tenant.</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyBeginResult> Begin(string key, string operation, string requestHash, CancellationToken cancellationToken);

    Task Complete(string key, IdempotentResponse response, CancellationToken cancellationToken);

    /// <summary>Releases the key after a failure that the client may retry (5xx).</summary>
    Task Release(string key, CancellationToken cancellationToken);
}
