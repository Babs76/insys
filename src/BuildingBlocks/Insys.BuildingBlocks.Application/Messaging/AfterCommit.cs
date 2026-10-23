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
namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>
/// Work that must happen only once the command's transaction has committed, such as evicting a cache entry. It runs
/// in the same process; other processes learn of the change through the command's events.
/// </summary>
public interface IAfterCommit
{
    void Enqueue(Action action);
}

public sealed class AfterCommitActions : IAfterCommit
{
    private readonly List<Action> _actions = [];

    public void Enqueue(Action action) => _actions.Add(action);

    public void RunAll()
    {
        foreach (var action in _actions)
        {
            action();
        }

        _actions.Clear();
    }

    public void Discard() => _actions.Clear();
}
