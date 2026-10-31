/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Infrastructure
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
    RequestPreconditions preconditions,
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

        var expected = preconditions.Take();
        var attempts = command is IRetryOnConflict && expected is null ? Math.Max(1, retry.Value.MaxAttempts) : 1;
        for (var attempt = 1; ; attempt++)
        {
            var (result, conflicted) = await Attempt(command, expected, retryable: attempt < attempts, cancellationToken);
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

    private async Task<(Result<TResult> Result, bool Conflicted)> Attempt(TCommand command, long? expected, bool retryable, CancellationToken cancellationToken)
    {
        Result<TResult> result;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                result = await inner.Handle(command, cancellationToken);
                // A stale If-Match is answered first, as HTTP evaluates preconditions before the request, even when the change was refused.
                if (expected is { } version && Stale(version) is { } stale)
                {
                    result = stale;
                }

                if (result.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                else
                {
                    var entries = auditTrail.TakeAll();
                    db.PublishAudit([.. entries, .. db.RecordChanges(typeof(TCommand).Name, entries)], AuditOutcome.Success);
                    var candidates = SavedVersions.Candidates(db);
                    await db.SaveChangesAsync(cancellationToken);
                    result = await SavedVersions.Apply(command, result, candidates, retry.Value.RequireSavedVersion, cancellationToken);
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

    /// <summary>
    /// If-Match taken by the endpoint (<see cref="RequestPreconditions"/>): the aggregate the command changed must be at the
    /// version the caller read, as it was loaded. A command changes one aggregate (the modified one, or else the only one it
    /// loaded); a stale version is 412 and nothing is written. The update itself still checks row_version, so a change
    /// committed meanwhile is a conflict too.
    /// </summary>
    private Error? Stale(long expected)
    {
        var roots = db.ChangeTracker.Entries().Where(e => e.Entity is BuildingBlocks.Domain.IAggregateRoot && e.Metadata.FindProperty("RowVersion") is not null).ToList();
        var changed = roots.Where(e => e.State == EntityState.Modified).ToList();
        var target = changed.Count == 1 ? changed[0] : changed.Count == 0 && roots.Count(e => e.State == EntityState.Unchanged) == 1
            ? roots.Single(e => e.State == EntityState.Unchanged) : null;
        if (target is null)
        {
            logger.LogWarning("{Command} took If-Match but changed {Count} aggregates; the version is not compared", typeof(TCommand).Name, changed.Count);
            return null;
        }

        return target.Property("RowVersion").OriginalValue is long loaded && loaded != expected
            ? Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The record has changed since it was read. Read it again and retry.")
            : null;
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

/// <summary>
/// A command's response carries the version of the record as saved, read back from the row in the same transaction after the save, however many times it was written and whatever else
/// touched it. Every response with a rowVersion or an ETag gets it here, so no handler computes one: the view a handler builds is from before the save. Which record: the tracked one
/// whose id the view carries; for a version returned as a number, the one aggregate the command changed.
/// </summary>
internal static class SavedVersions
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (System.Reflection.PropertyInfo Version, System.Reflection.PropertyInfo? Id)?> Views = new();

    /// <summary>What the command tracked, taken before the save, which marks everything unchanged: the records with a version, those it changed, and their versions as loaded.</summary>
    public static SavedRecords Candidates(DbContext db)
    {
        var tracked = db.ChangeTracker.Entries().Where(e => e.Metadata.FindProperty("RowVersion") is not null && e.State != EntityState.Deleted).ToList();
        var changed = tracked.Where(e => e.State is EntityState.Modified or EntityState.Added).ToList();
        return new SavedRecords(tracked, changed, changed.ToDictionary(e => e, e => (long)e.Property("RowVersion").OriginalValue!));
    }

    public static async Task<Result<TResult>> Apply<TCommand, TResult>(TCommand command, Result<TResult> result,
        SavedRecords saved, bool require, CancellationToken cancellationToken)
        where TCommand : notnull
    {
        if (result.Value is long returned && command is IVersionedCommand or IReturnsSavedVersion)
        {
            var target = TheRecord(command, returned, saved);
            if (target is null)
            {
                return require ? throw new InvalidOperationException($"{typeof(TCommand).Name} returned version {returned}, but its record can't be told among: {string.Join(", ", saved.Changed.Select(e => e.Metadata.ClrType.Name))}.") : result;
            }

            return (TResult)(object)await Read(target, cancellationToken);
        }

        if (result.Value is not { } view || Views.GetOrAdd(view.GetType(), Shape) is not { } shape)
        {
            return result;
        }

        var id = shape.Id?.GetValue(view);
        var record = id is null ? null : saved.Tracked.FirstOrDefault(e => e.Metadata.FindProperty("Id") is not null && Equals(e.Property("Id").CurrentValue, id));
        record ??= saved.ChangedRoots.Count == 1 ? saved.ChangedRoots[0] : null;
        if (record is null)
        {
            // Nothing this command saved is the view's: it was read, so its version is current.
            return result;
        }

        shape.Version.SetValue(view, await Read(record, cancellationToken));
        return result;
    }

    /// <summary>
    /// The record a returned version is of. The handler returned that record's version as loaded, which with what the command changed, and the id it carries, tells it: the changed aggregate
    /// (or with none, changed record) that fits; with no change at all, the aggregate the command names or the only one it loaded.
    /// </summary>
    private static Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry? TheRecord<TCommand>(TCommand command, long returned, SavedRecords saved)
        where TCommand : notnull
    {
        var ids = typeof(TCommand).GetProperties().Where(p => p.PropertyType == typeof(Guid)).Select(p => p.GetValue(command)).ToHashSet();
        bool Named(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry e) => e.Metadata.FindProperty("Id") is not null && ids.Contains(e.Property("Id").CurrentValue);
        var pool = saved.ChangedRoots.Count > 0 ? saved.ChangedRoots : saved.Changed;
        if (pool.Count > 0)
        {
            var fits = pool.Count == 1 ? pool : pool.Where(e => saved.Loaded[e] == returned).ToList();
            return fits.Count == 1 ? fits[0] : fits.Where(Named).ToList() is { Count: 1 } named ? named[0] : null;
        }

        var roots = saved.Tracked.Where(e => e.Entity is IAggregateRoot).ToList();
        return roots.Where(Named).ToList() is { Count: 1 } one ? one[0] : roots.Count == 1 ? roots[0] : saved.Tracked.Where(Named).FirstOrDefault();
    }

    private static async Task<long> Read(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, CancellationToken cancellationToken) =>
        (await entry.GetDatabaseValuesAsync(cancellationToken))?.GetValue<long>("RowVersion") ?? (long)entry.Property("RowVersion").CurrentValue!;

    private static (System.Reflection.PropertyInfo, System.Reflection.PropertyInfo?)? Shape(Type type) =>
        type.GetProperty("RowVersion") is { CanWrite: true, PropertyType: var t } version && t == typeof(long) ? (version, type.GetProperty("Id")) : null;
}

internal sealed record SavedRecords(List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry> Tracked, List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry> Changed,
    Dictionary<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry, long> Loaded)
{
    public List<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry> ChangedRoots { get; } = Changed.Where(e => e.Entity is IAggregateRoot).ToList();
}

/// <summary>How often a command marked <see cref="IRetryOnConflict"/> is attempted (configuration Commands:ConflictRetry).</summary>
public sealed class ConflictRetryOptions
{
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Tests only: a command returning a version whose aggregate can't be told is an error, so every flow the suites run proves it.</summary>
    public bool RequireSavedVersion { get; set; }
}
