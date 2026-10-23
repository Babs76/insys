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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Tests;

public sealed class StateMachineTests
{
    private enum State { Draft, Active, Closed }

    private enum Trigger { Activate, Close }

    private static readonly StateMachine<State, Trigger> Machine = new StateMachine<State, Trigger>()
        .Allow(State.Draft, Trigger.Activate, State.Active)
        .Allow(State.Active, Trigger.Close, State.Closed);

    [Fact]
    public void StateMachine_AllowedTransition_ReturnsTarget() =>
        Machine.Fire(State.Draft, Trigger.Activate).Value.ShouldBe(State.Active);

    [Fact]
    public void StateMachine_OtherTransition_IsInvalidStateTransitionConflict()
    {
        var result = Machine.Fire(State.Draft, Trigger.Close);

        result.Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void MakerChecker_SamePerson_IsForbidden_SRS002()
    {
        MakerChecker.EnsureDifferentPeople("user-1", "USER-1").Error!.Code.ShouldBe(ErrorCodes.MakerCheckerSamePerson);
        MakerChecker.EnsureDifferentPeople("user-1", "user-2").IsSuccess.ShouldBeTrue();
    }
}
