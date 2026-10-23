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
using System.Collections.Concurrent;
using Insys.BuildingBlocks.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>
/// Resolves the handler for a request from DI. Handlers are registered already wrapped in their pipeline
/// (validation, then the module's transaction) by the module's Infrastructure registration.
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    public Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = (Invoker<TResult>)Invokers.GetOrAdd(
            command.GetType(),
            type => Activator.CreateInstance(typeof(CommandInvoker<,>).MakeGenericType(type, typeof(TResult)))!);
        return invoker.Invoke(command, services, cancellationToken);
    }

    public Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var invoker = (Invoker<TResult>)Invokers.GetOrAdd(
            query.GetType(),
            type => Activator.CreateInstance(typeof(QueryInvoker<,>).MakeGenericType(type, typeof(TResult)))!);
        return invoker.Invoke(query, services, cancellationToken);
    }

    private abstract class Invoker<TResult>
    {
        public abstract Task<Result<TResult>> Invoke(object request, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class CommandInvoker<TCommand, TResult> : Invoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<Result<TResult>> Invoke(object request, IServiceProvider services, CancellationToken cancellationToken) =>
            services.GetRequiredService<ICommandHandler<TCommand, TResult>>().Handle((TCommand)request, cancellationToken);
    }

    private sealed class QueryInvoker<TQuery, TResult> : Invoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<Result<TResult>> Invoke(object request, IServiceProvider services, CancellationToken cancellationToken) =>
            services.GetRequiredService<IQueryHandler<TQuery, TResult>>().Handle((TQuery)request, cancellationToken);
    }
}
