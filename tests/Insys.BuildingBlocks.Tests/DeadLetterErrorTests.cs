/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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
using Insys.BuildingBlocks.Application.Messaging;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// BR-017 (D-28): a dead letter's error, which operations staff read, never holds personal data or the message body. It is
/// made only from an exception's type, an HTTP status or a fixed sentence, never an exception's message, which can quote
/// the payload.
/// </summary>
public sealed class DeadLetterErrorTests
{
    private const string Personal = "Jane Doe, ID 8001015009087, jane@example.com";

    [Fact]
    public void AnOutboxFailure_IsNamedByTheExceptionsType_NeverItsMessage_BR017()
    {
        var error = DeadLetterErrors.Of(new InvalidOperationException($"Could not publish {{\"name\":\"{Personal}\"}}"));

        error.ShouldBe("System.InvalidOperationException");
        error.ShouldNotContain("Jane");
    }

    [Fact]
    public void AnAggregateFailure_IsNamedByEachInnerExceptionsType_BR017()
    {
        var error = DeadLetterErrors.Of(new AggregateException(new ArgumentException(Personal), new TimeoutException(Personal)));

        error.ShouldBe("System.ArgumentException; System.TimeoutException");
        error.ShouldNotContain("8001015009087");
    }

    [Fact]
    public void AWebhookFailure_IsTheExceptionsShortType_OrTheHttpStatus_BR017()
    {
        DeadLetterErrors.NoResponse(new HttpRequestException($"Connection refused (https://hooks.example.com/?member={Personal})")).ShouldBe("HttpRequestException");
        DeadLetterErrors.NoResponse(new TaskCanceledException(Personal)).ShouldBe("TaskCanceledException");
        DeadLetterErrors.Status(503).ShouldBe("HTTP 503");
    }

    [Fact]
    public void TheParkingLedgersError_IsAFixedSentence_BR017() =>
        DeadLetterErrors.AttemptLimitReached.ShouldBe("Delivery failed after the attempt limit; see the consumer's logs.");
}
