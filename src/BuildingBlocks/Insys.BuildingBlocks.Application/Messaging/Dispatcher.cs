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
