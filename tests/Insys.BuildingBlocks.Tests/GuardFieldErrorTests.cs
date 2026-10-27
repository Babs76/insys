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
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Tests;

/// <summary>
/// BR-073 (defect): a reason or code longer than the domain stores used to throw an ArgumentException, a 500. A missing or
/// too long guarded value is now a field error the API answers with 422 VALIDATION_FAILED, naming the field as the request does.
/// </summary>
public sealed class GuardFieldErrorTests
{
    private sealed record Command(string Reason, string? RuleCode);

    [Fact]
    public void ATooLongValue_IsAFieldError_NamedAsTheRequestNamesIt_BR073()
    {
        var command = new Command(new string('x', 31), null);
        var reason = command.Reason;

        Should.Throw<GuardException>(() => Guard.MaxLength(reason, 30)).Field.ShouldBe("reason");
        var nested = Should.Throw<GuardException>(() => Guard.MaxLength(Guard.NotNullOrWhiteSpace(command.Reason).Trim(), 30));
        (nested.Field, nested.Problem).ShouldBe(("reason", "reason must be at most 30 characters."));
        Should.Throw<GuardException>(() => Guard.NotNullOrWhiteSpace(command.RuleCode)).Problem.ShouldBe("ruleCode is required.");
    }

    [Fact]
    public void AGuardFailure_IsAnswered422_NotA500_BR073()
    {
        var failure = Should.Throw<GuardException>(() => Guard.MaxLength(new string('x', 21), 20));

        InsysExceptionHandler.Classify(failure).ShouldBe((422, ErrorCodes.ValidationFailed, "The request is not valid."));
        failure.Problem.Contains("xxx", StringComparison.Ordinal).ShouldBeFalse("the message never holds the value");
    }
}
