/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
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
namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// An aggregate's allowed transitions, copied from its stateTransitions list in domain-model.json.
/// Any other transition fails with INVALID_STATE_TRANSITION (409).
/// </summary>
public sealed class StateMachine<TState, TTrigger>
    where TState : struct, Enum
    where TTrigger : struct, Enum
{
    private readonly Dictionary<(TState From, TTrigger Trigger), TState> _transitions = [];

    public StateMachine<TState, TTrigger> Allow(TState from, TTrigger trigger, TState to)
    {
        _transitions.Add((from, trigger), to);
        return this;
    }

    public IEnumerable<(TState From, TTrigger Trigger, TState To)> Transitions =>
        _transitions.Select(t => (t.Key.From, t.Key.Trigger, t.Value));

    public bool CanFire(TState from, TTrigger trigger) => _transitions.ContainsKey((from, trigger));

    public Result<TState> Fire(TState from, TTrigger trigger) =>
        _transitions.TryGetValue((from, trigger), out var to)
            ? to
            : Error.Conflict(ErrorCodes.InvalidStateTransition, $"{trigger} is not allowed when the state is {from}.");
}
