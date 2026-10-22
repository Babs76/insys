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
