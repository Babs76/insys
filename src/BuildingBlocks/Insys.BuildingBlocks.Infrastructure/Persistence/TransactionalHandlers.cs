using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Runs a command in one transaction on the module's DbContext: the tenant is set first (interceptor), the handler
/// changes one aggregate, SaveChanges writes the aggregate and its outbox events, and recorded audit entries are
/// written before commit. A failed result rolls everything back.
/// </summary>
public sealed class TransactionalCommandHandler<TCommand, TResult, TDbContext>(
    ICommandHandler<TCommand, TResult> inner,
    TDbContext db,
    AuditTrail auditTrail,
    AuditEventWriter auditWriter,
    ITenantContext tenant,
    IExecutionContext execution) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
    where TDbContext : ModuleDbContext
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Result<TResult> result;
        try
        {
            result = await inner.Handle(command, cancellationToken);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                await auditWriter.WriteIndependently(auditTrail.TakeAll(), AuditOutcome.Failure, tenantId, execution, cancellationToken);
                return result;
            }

            await db.SaveChangesAsync(cancellationToken);
            var audit = auditTrail.TakeAll();
            if (audit.Count > 0)
            {
                await auditWriter.Write((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction(),
                    audit, AuditOutcome.Success, tenantId, execution, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await auditWriter.WriteIndependently(auditTrail.TakeAll(), AuditOutcome.Failure, tenantId, execution, CancellationToken.None);
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The record was changed by another request. Read it again and retry.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await auditWriter.WriteIndependently(auditTrail.TakeAll(), AuditOutcome.Failure, tenantId, execution, CancellationToken.None);
            throw;
        }

        return result;
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await inner.Handle(query, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
