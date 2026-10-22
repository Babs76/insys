using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Runs a command in one transaction on the module's DbContext: the tenant is set first (interceptor), the handler
/// changes one aggregate, and SaveChanges writes the aggregate, its events and its audit entries to the module's
/// own schema. A failed result rolls everything back and publishes the audit entries with outcome Failure in a
/// separate transaction. Work queued with <see cref="IAfterCommit"/> runs only after a successful commit. A command that
/// is safe to repeat (<see cref="IRetryOnConflict"/>) is run again from the start, in a new transaction, when it meets a
/// concurrent change (an optimistic-lock conflict, a duplicate key, a serialization failure or a deadlock), up to
/// <see cref="ConflictRetryOptions.MaxAttempts"/>; then the conflict is returned.
/// </summary>
public sealed class TransactionalCommandHandler<TCommand, TResult, TDbContext>(
    ICommandHandler<TCommand, TResult> inner,
    TDbContext db,
    AuditTrail auditTrail,
    AfterCommitActions afterCommit,
    IOptions<ConflictRetryOptions> retry,
    ILogger<TransactionalCommandHandler<TCommand, TResult, TDbContext>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
    where TDbContext : ModuleDbContext
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        if (command is ITargetTenantRequest target)
        {
            db.ActAsTenant(target.TargetTenantId);
        }

        var attempts = command is IRetryOnConflict ? Math.Max(1, retry.Value.MaxAttempts) : 1;
        for (var attempt = 1; ; attempt++)
        {
            var (result, conflicted) = await Attempt(command, retryable: attempt < attempts, cancellationToken);
            if (!conflicted || attempt >= attempts)
            {
                return await Finish(result, cancellationToken);
            }

            // Start again from what is committed: nothing of the failed attempt is kept.
            db.ChangeTracker.Clear();
            auditTrail.TakeAll();
            afterCommit.Discard();
            logger.LogInformation("{Command} met a concurrent change; attempt {Attempt} of {Attempts}", typeof(TCommand).Name, attempt + 1, attempts);
        }
    }

    private async Task<(Result<TResult> Result, bool Conflicted)> Attempt(TCommand command, bool retryable, CancellationToken cancellationToken)
    {
        Result<TResult> result;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                result = await inner.Handle(command, cancellationToken);
                if (result.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                else
                {
                    db.PublishAudit(auditTrail.TakeAll(), AuditOutcome.Success);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return (Error.Conflict(ErrorCodes.ConcurrencyConflict, "The record was changed by another request. Read it again and retry."), true);
            }
            catch (Exception ex) when (retryable && Concurrent(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return (Error.Conflict(ErrorCodes.ConcurrencyConflict, "The record was changed by another request."), true);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                afterCommit.Discard();
                await PublishFailureAudit(CancellationToken.None);
                throw;
            }
        }

        return (result, false);
    }

    private async Task<Result<TResult>> Finish(Result<TResult> result, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            afterCommit.Discard();
            await PublishFailureAudit(cancellationToken);
            return result;
        }

        afterCommit.RunAll();
        return result;
    }

    /// <summary>A duplicate key (two requests opening the same row), a serialization failure or a deadlock.</summary>
    private static bool Concurrent(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected;

    /// <summary>Records the failed attempt (IAM-006) without any of the rolled-back changes.</summary>
    private async Task PublishFailureAudit(CancellationToken cancellationToken)
    {
        var entries = auditTrail.TakeAll();
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.PublishAudit(entries, AuditOutcome.Failure);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never hide the original failure; the type is logged, not the message, which may carry data values.
            logger.LogError("Could not record the failure audit for {Command}: {ExceptionType}", typeof(TCommand).Name, ex.GetType().Name);
        }
    }
}

/// <summary>Runs a query in a read-only transaction, so the tenant is set and row-level security applies to reads too.</summary>
public sealed class TransactionalQueryHandler<TQuery, TResult, TDbContext>(
    IQueryHandler<TQuery, TResult> inner,
    TDbContext db) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
    where TDbContext : ModuleDbContext
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        if (query is ITargetTenantRequest target)
        {
            db.ActAsTenant(target.TargetTenantId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await inner.Handle(query, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

/// <summary>How often a command marked <see cref="IRetryOnConflict"/> is attempted (configuration Commands:ConflictRetry).</summary>
public sealed class ConflictRetryOptions
{
    public int MaxAttempts { get; set; } = 3;
}
