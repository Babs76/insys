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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>A request to change one aggregate. Runs in one transaction with the outbox.</summary>
public interface ICommand<TResult>;

/// <summary>
/// A command that is safe to send again: the pipeline retries it, from the start and in a new transaction, when it
/// meets a concurrent change (optimistic lock, duplicate key, serialization failure, deadlock), up to a small limit.
/// </summary>
public interface IRetryOnConflict;

/// <summary>A command on an existing aggregate that carries the version from If-Match (DBA-011).</summary>
public interface IVersionedCommand
{
    long ExpectedVersion { get; }
}

/// <summary>A request to read. Runs in a read-only transaction so row-level security applies.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Sends commands and queries to their handlers through the validation and transaction pipeline.</summary>
public interface IDispatcher
{
    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

/// <summary>An empty success value for commands that return nothing.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
}
