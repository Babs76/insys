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
using System.Text.Json;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Contracts.StaticData;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure.StaticData;

/// <summary>Error codes for static data governance.</summary>
public static class StaticDataErrorCodes
{
    [ErrorCode(409, "error.static_data.draft_exists")]
    public const string DraftExists = "STATIC_DRAFT_EXISTS";

    [ErrorCode(403, "error.static_data.maker_only")]
    public const string MakerOnly = "STATIC_MAKER_ONLY";

    [ErrorCode(422, "error.static_data.scope_not_allowed")]
    public const string ScopeNotAllowed = "STATIC_SCOPE_NOT_ALLOWED";

    [ErrorCode(422, "error.static_data.retroactive_change")]
    public const string RetroactiveChange = "STATIC_RETROACTIVE_CHANGE";

    [ErrorCode(422, "error.static_data.import_rejected")]
    public const string ImportRejected = "STATIC_IMPORT_REJECTED";

    [ErrorCode(403, "error.static_data.user_required")]
    public const string UserRequired = "STATIC_USER_REQUIRED";
}

/// <summary>Creates a module context on the platform administrator's connection, for global rows only.</summary>
public sealed class StaticDataContextFactory<TDbContext>(IServiceProvider services)
    where TDbContext : ModuleDbContext
{
    public TDbContext CreatePlatformAdmin()
    {
        var options = new DbContextOptionsBuilder<TDbContext>()
            .UseNpgsql(services.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKeys.PlatformAdmin), npgsql => npgsql.UseNodaTime())
            .AddInterceptors(services.GetRequiredService<TenantTransactionInterceptor>())
            .Options;
        return ActivatorUtilities.CreateInstance<TDbContext>(services, options);
    }
}

/// <summary>
/// The static data lifecycle for any generated entity (SDM-002 to SDM-015). Tenant rows are written on the owning
/// module's connection; global rows (tenant_id IS NULL) only on the platform administrator's connection, which the
/// API allows only for the platform operator tenant. Approval itself happens in the Static Data module, which checks
/// that maker and checker differ; this service applies the outcome from its events.
/// </summary>
public sealed class StaticDataService<TDbContext>(
    TDbContext tenantDb,
    StaticDataContextFactory<TDbContext> contexts,
    IExecutionContext execution,
    IBusinessDate businessDate, IClock clock,
    IFieldProtector protector,
    IEnumerable<IStaticChangeGuard> guards,
    IEnumerable<IStaticRecordValidator> validators)
    where TDbContext : ModuleDbContext
{
    public async Task<IReadOnlyList<TEntity>> ListInForce<TEntity>(StaticDataDescriptor descriptor, DateOnly asAt, string? afterCode, int limit,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        await using var transaction = await ReadOnly(tenantDb, cancellationToken);
        var query = InForce(tenantDb.Set<TEntity>(), descriptor, asAt);
        if (afterCode is not null)
        {
            query = query.Where(r => string.Compare(r.Code, afterCode) > 0);
        }

        // Fetch both global and tenant rows for each code, then let the tenant row override the baseline.
        var rows = await query.OrderBy(r => r.Code).ThenBy(r => r.TenantId == null).Take(limit * 2).ToListAsync(cancellationToken);
        return Overlay(rows).Take(limit).ToList();
    }

    public async Task<TEntity?> GetInForce<TEntity>(StaticDataDescriptor descriptor, string code, DateOnly asAt, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        await using var transaction = await ReadOnly(tenantDb, cancellationToken);
        var rows = await InForce(tenantDb.Set<TEntity>(), descriptor, asAt).Where(r => r.Code == code).ToListAsync(cancellationToken);
        return Overlay(rows).FirstOrDefault();
    }

    public async Task<IReadOnlyList<TEntity>> Versions<TEntity>(string code, bool global, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        await using var lease = Lease(global);
        await using var transaction = await ReadOnly(lease.Context, cancellationToken);
        return await Scoped(lease.Context.Set<TEntity>(), global).Where(r => r.Code == code).OrderBy(r => r.Version).ToListAsync(cancellationToken);
    }

    public Task<Result<TEntity>> CreateDraft<TEntity, TInput>(StaticDataDescriptor descriptor, TInput input, bool global, CancellationToken cancellationToken)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput =>
        Write<TEntity>(global, "CreateDraft", descriptor, async (db, audit) =>
        {
            var checks = CheckWrite<TEntity, TInput>(descriptor, input);
            if (checks is not null)
            {
                return checks;
            }

            var existing = await Scoped(db.Set<TEntity>(), global).Where(r => r.Code == input.Code).ToListAsync(cancellationToken);
            if (existing.Any(r => r.RecordStatus == descriptor.Lifecycle.Draft || descriptor.Lifecycle.Submitted.Contains(r.RecordStatus)
                    || r.RecordStatus == descriptor.Lifecycle.Approved))
            {
                return Error.Conflict(StaticDataErrorCodes.DraftExists, "This code already has a version in progress. Finish or discard it first.");
            }

            var current = existing.Where(r => descriptor.Lifecycle.IsInForce(r.RecordStatus)).MaxBy(r => r.Version);
            var entity = new TEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = global ? null : db.CurrentTenantId,
                Code = input.Code,
                Version = existing.Count == 0 ? 1 : existing.Max(r => r.Version) + 1,
                EffectiveFrom = input.EffectiveFrom ?? businessDate.Today,
                RecordStatus = descriptor.Lifecycle.Draft,
                SupersedesId = current?.Id,
            };
            entity.ApplyCommon(input);
            entity.ApplyFields(input, protector);
            db.Set<TEntity>().Add(entity);
            audit(entity, null);
            return entity;
        }, cancellationToken);

    public Task<Result<TEntity>> EditDraft<TEntity, TInput>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, TInput input,
        bool global, CancellationToken cancellationToken)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput =>
        Write<TEntity>(global, "EditDraft", descriptor, async (db, audit) =>
        {
            var checks = CheckWrite<TEntity, TInput>(descriptor, input);
            if (checks is not null)
            {
                return checks;
            }

            var loaded = await LoadForChange<TEntity>(db, code, version, expectedRowVersion, global, cancellationToken);
            if (loaded.IsFailure)
            {
                return loaded.Error;
            }

            var entity = loaded.Value;
            if (entity.RecordStatus != descriptor.Lifecycle.Draft)
            {
                return InvalidTransition("Only drafts can be edited; approved versions are immutable (SDM-002).");
            }

            if (input.Code != entity.Code)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, "The code of a version cannot change.");
            }

            if (entity.CreatedBy != execution.ActorId)
            {
                return Error.Forbidden(StaticDataErrorCodes.MakerOnly, "Only the maker who created the draft edits it, so the maker of the change stays known (SDM-003).");
            }

            var before = Snapshot(entity);
            entity.ApplyCommon(input);
            entity.ApplyFields(input, protector);
            audit(entity, before);
            return entity;
        }, cancellationToken);

    /// <summary>Draft → pending approval. Raises StaticDataChangeSubmitted; the Static Data module records the change request.</summary>
    public Task<Result<TEntity>> Submit<TEntity>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord =>
        Write<TEntity>(global, "SubmitChange", descriptor, async (db, audit) =>
        {
            var loaded = await LoadForChange<TEntity>(db, code, version, expectedRowVersion, global, cancellationToken);
            if (loaded.IsFailure)
            {
                return loaded.Error;
            }

            var entity = loaded.Value;
            if (entity.RecordStatus != descriptor.Lifecycle.Draft)
            {
                return InvalidTransition($"Only drafts can be submitted; this version is {entity.RecordStatus}.");
            }

            if (entity.CreatedBy != execution.ActorId)
            {
                return Error.Forbidden(StaticDataErrorCodes.MakerOnly, "Only the maker who created the draft submits it (SDM-003).");
            }

            // SDM-013 again on submit: the new period must start after the version it supersedes, and Tier 1 data that is
            // already in force must not change retroactively (SDM-005).
            var current = entity.SupersedesId is { } supersedes
                ? await db.Set<TEntity>().SingleOrDefaultAsync(r => r.Id == supersedes, cancellationToken)
                : null;
            if (current is not null && entity.EffectiveFrom <= current.EffectiveFrom)
            {
                return Error.Validation(StaticDataErrorCodes.RetroactiveChange, "effectiveFrom must be after the start of the version in force.");
            }

            if (current is not null && descriptor.Tier == 1 && entity.EffectiveFrom < businessDate.Today)
            {
                return Error.Validation(StaticDataErrorCodes.RetroactiveChange,
                    "Tier 1 data in force cannot change retroactively; use the correction workflow (SDM-005).");
            }

            // A module's own rules for a change to one of its tables (an intermediary's lifecycle, say).
            foreach (var guard in guards.Where(g => g.Schema == descriptor.Schema && g.Table == descriptor.Table))
            {
                var allowed = guard.Check(entity, current);
                if (allowed.IsFailure)
                {
                    return allowed.Error;
                }
            }

            entity.RecordStatus = descriptor.Lifecycle.PendingApproval;
            entity.ChangeRequestId = Guid.CreateVersion7();
            db.Publish(new StaticDataChangeSubmitted(entity.ChangeRequestId.Value, Ref(descriptor, entity), entity.SupersedesId is null ? "CREATE" : "UPDATE",
                Guid.Parse(entity.CreatedBy), descriptor.Tier, descriptor.Scope.ToString(), entity.EffectiveFrom, entity.ChangeReason ?? ""),
                descriptor.EntityName, entity.Id, clock.UtcNow);
            audit(entity, null);
            return entity;
        }, cancellationToken);

    /// <summary>Approved → active (reference) or published (configuration). Raises StaticDataVersionActivated (SDM-009).</summary>
    public Task<Result<TEntity>> Activate<TEntity>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord =>
        Transition<TEntity>(descriptor, code, version, expectedRowVersion, global, "ActivateVersion", [descriptor.Lifecycle.Approved], descriptor.Lifecycle.Active,
            (db, entity) => db.Publish(new StaticDataVersionActivated(entity.ChangeRequestId, Ref(descriptor, entity), entity.EffectiveFrom, entity.EffectiveTo),
                descriptor.EntityName, entity.Id, clock.UtcNow),
            cancellationToken);

    public Task<Result<TEntity>> Deprecate<TEntity>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord =>
        Transition<TEntity>(descriptor, code, version, expectedRowVersion, global, "DeprecateCode", descriptor.Lifecycle.InForce, descriptor.Lifecycle.Deprecated,
            (db, entity) => db.Publish(new StaticDataCodeDeprecated(entity.ChangeRequestId, Ref(descriptor, entity)), descriptor.EntityName, entity.Id, clock.UtcNow),
            cancellationToken);

    public Task<Result<TEntity>> Retire<TEntity>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord =>
        Transition<TEntity>(descriptor, code, version, expectedRowVersion, global, "RetireCode",
            descriptor.Lifecycle == StaticLifecycle.Reference ? [descriptor.Lifecycle.Deprecated] : [.. descriptor.Lifecycle.InForce, descriptor.Lifecycle.Deprecated],
            descriptor.Lifecycle.Retired, (_, _) => { }, cancellationToken);

    /// <summary>
    /// Applies an approval decided by the Static Data module. The version it supersedes is closed at the new version's
    /// start, because approved versions already count for the no-overlap rule (SDM-004). Idempotent.
    /// </summary>
    public async Task ApplyApproval<TEntity>(StaticDataDescriptor descriptor, StaticDataChangeApproved approved, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        await ApplyDecision<TEntity>(approved.Record, async db =>
        {
            var entity = await db.Set<TEntity>().SingleOrDefaultAsync(r => r.Id == approved.Record.RecordId, cancellationToken);
            if (entity is null || entity.RecordStatus != descriptor.Lifecycle.PendingApproval || entity.ChangeRequestId != approved.ChangeRequestId)
            {
                return;
            }

            if (entity.SupersedesId is { } supersedes
                && await db.Set<TEntity>().SingleOrDefaultAsync(r => r.Id == supersedes, cancellationToken) is { } previous
                && (previous.EffectiveTo is null || previous.EffectiveTo > entity.EffectiveFrom))
            {
                previous.EffectiveTo = entity.EffectiveFrom;
            }

            entity.RecordStatus = descriptor.Lifecycle.Approved;
            entity.ApprovedAt = approved.ApprovedAt;
            entity.ApprovedBy = approved.CheckerId.ToString();
        }, cancellationToken);
    }

    /// <summary>Applies a rejection: the version goes back to draft for the maker to correct. Idempotent.</summary>
    public async Task ApplyRejection<TEntity>(StaticDataDescriptor descriptor, StaticDataChangeRejected rejected, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        await ApplyDecision<TEntity>(rejected.Record, async db =>
        {
            var entity = await db.Set<TEntity>().SingleOrDefaultAsync(r => r.Id == rejected.Record.RecordId, cancellationToken);
            if (entity is not null && entity.RecordStatus == descriptor.Lifecycle.PendingApproval && entity.ChangeRequestId == rejected.ChangeRequestId)
            {
                entity.RecordStatus = descriptor.Lifecycle.Draft;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates drafts for every line, all or nothing (SDM-012). Each line is validated, then inserted under a savepoint
    /// so every rejected line is reported, not just the first.
    /// </summary>
    public async Task<Result<int>> Import<TEntity, TInput>(StaticDataDescriptor descriptor, IReadOnlyList<TInput> lines, string fileRef, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        if (RequireUser() is { } userError)
        {
            return userError;
        }

        var errors = new List<ImportLineError>();
        for (var i = 0; i < lines.Count; i++)
        {
            var scopeError = CheckScope(descriptor, global);
            foreach (var (field, messages) in lines[i].Validate().Concat(ValidateRecord<TEntity, TInput>(descriptor, lines[i])))
            {
                errors.AddRange(messages.Select(m => new ImportLineError(i + 1, field, m)));
            }

            if (scopeError is not null)
            {
                errors.Add(new ImportLineError(i + 1, "scope", scopeError.Message));
            }
        }

        var batchId = Guid.CreateVersion7();
        await using var lease = Lease(global);
        var context = lease.Context;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existingCodes = await Scoped(context.Set<TEntity>(), global).Select(r => r.Code).Distinct().ToListAsync(cancellationToken);
        if (errors.Count == 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (existingCodes.Contains(lines[i].Code) || lines.Take(i).Any(l => l.Code == lines[i].Code))
                {
                    errors.Add(new ImportLineError(i + 1, "code", "The code already exists; change existing codes through a new version."));
                    continue;
                }

                var entity = new TEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = global ? null : context.CurrentTenantId,
                    Code = lines[i].Code,
                    Version = 1,
                    EffectiveFrom = lines[i].EffectiveFrom ?? businessDate.Today,
                    RecordStatus = descriptor.Lifecycle.Draft,
                };
                entity.ApplyCommon(lines[i]);
                entity.ApplyFields(lines[i], protector);
                await transaction.CreateSavepointAsync("line", cancellationToken);
                context.Set<TEntity>().Add(entity);
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
                {
                    await transaction.RollbackToSavepointAsync("line", cancellationToken);
                    context.Entry(entity).State = EntityState.Detached;
                    errors.Add(new ImportLineError(i + 1, postgres.ColumnName ?? postgres.ConstraintName ?? "row", DescribeDatabaseError(postgres)));
                }
            }
        }

        var submittedBy = Guid.Parse(execution.ActorId);
        if (errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();
            await using var report = await context.Database.BeginTransactionAsync(cancellationToken);
            context.Publish(new StaticDataImportRejected(batchId, descriptor.EntityCode, fileRef, lines.Count, submittedBy, errors),
                "ImportBatch", batchId, clock.UtcNow);
            await context.SaveChangesAsync(cancellationToken);
            await report.CommitAsync(cancellationToken);
            return Error.Validation(StaticDataErrorCodes.ImportRejected, $"The import was rejected: {errors.Count} line error(s). Nothing was saved.",
                errors.GroupBy(e => $"line {e.Line}").ToDictionary(g => g.Key, g => g.Select(e => $"{e.Field}: {e.Message}").ToArray()));
        }

        context.Publish(new StaticDataImportCompleted(batchId, descriptor.EntityCode, fileRef, lines.Count, submittedBy), "ImportBatch", batchId, clock.UtcNow);
        context.PublishAudit([new AuditEntry("STATIC_DATA", "CommitImport", descriptor.EntityCode, batchId.ToString(),
            new Dictionary<string, string?> { ["rows"] = lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["global"] = global.ToString() })],
            AuditOutcome.Success);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lines.Count;
    }

    public static StaticRecordRef Ref(StaticDataDescriptor descriptor, StaticRecord entity) =>
        new(descriptor.Schema, descriptor.Table, descriptor.EntityCode, descriptor.ModuleRoute, descriptor.EntityRoute, entity.Id, entity.Code, entity.Version,
            entity.TenantId is null);

    private Task<Result<TEntity>> Transition<TEntity>(StaticDataDescriptor descriptor, string code, int version, long expectedRowVersion, bool global,
        string operation, string[] from, string to, Action<TDbContext, TEntity> publish, CancellationToken cancellationToken)
        where TEntity : StaticRecord =>
        Write<TEntity>(global, operation, descriptor, async (db, audit) =>
        {
            var loaded = await LoadForChange<TEntity>(db, code, version, expectedRowVersion, global, cancellationToken);
            if (loaded.IsFailure)
            {
                return loaded.Error;
            }

            var entity = loaded.Value;
            if (!from.Contains(entity.RecordStatus))
            {
                return InvalidTransition($"{operation} is not allowed when the version is {entity.RecordStatus}.");
            }

            var before = entity.RecordStatus;
            entity.RecordStatus = to;
            publish(db, entity);
            audit(entity, before);
            return entity;
        }, cancellationToken);

    /// <summary>Runs one change in one transaction, with the audit entry and events in the same commit.</summary>
    private async Task<Result<TEntity>> Write<TEntity>(bool global, string operation, StaticDataDescriptor descriptor,
        Func<TDbContext, Action<TEntity, string?>, Task<Result<TEntity>>> change, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        if (RequireUser() is { } userError)
        {
            return userError;
        }

        if (CheckScope(descriptor, global) is { } scopeError)
        {
            return scopeError;
        }

        await using var lease = Lease(global);
        var context = lease.Context;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var entries = new List<AuditEntry>();
        var result = await change(context, (entity, before) => entries.Add(new AuditEntry("STATIC_DATA", operation, descriptor.EntityCode, entity.Id.ToString(),
            AuditDetails(descriptor, entity, before, global))));
        if (result.IsFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return result;
        }

        context.PublishAudit(entries, AuditOutcome.Success);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.ConcurrencyConflict, "The record was changed by another request. Read it again and retry.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task ApplyDecision<TEntity>(StaticRecordRef record, Func<TDbContext, Task> apply, CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        if (!record.Global)
        {
            // Runs inside the consumer's inbox transaction on the module context.
            await apply(tenantDb);
            return;
        }

        // A global row: only the platform administrator's connection may change it. The inbox row commits separately on
        // the module connection, which is safe because applying a decision twice changes nothing.
        await using var admin = contexts.CreatePlatformAdmin();
        await using var transaction = await admin.Database.BeginTransactionAsync(cancellationToken);
        await apply(admin);
        await admin.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Result<TEntity>> LoadForChange<TEntity>(TDbContext db, string code, int version, long expectedRowVersion, bool global,
        CancellationToken cancellationToken)
        where TEntity : StaticRecord
    {
        var entity = await Scoped(db.Set<TEntity>(), global).SingleOrDefaultAsync(r => r.Code == code && r.Version == version, cancellationToken);
        if (entity is null)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The static data version was not found.");
        }

        return entity.RowVersion == expectedRowVersion
            ? entity
            : Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The version has changed since it was read.");
    }

    private Error? RequireUser() =>
        execution.ActorType == ActorType.User && Guid.TryParse(execution.ActorId, out _)
            ? null
            : Error.Forbidden(StaticDataErrorCodes.UserRequired, "Static data changes are made by identified users (maker-checker, SDM-003).");

    private static Error? CheckScope(StaticDataDescriptor descriptor, bool global) =>
        global && !descriptor.AllowsGlobalRows
            ? Error.Validation(StaticDataErrorCodes.ScopeNotAllowed, $"{descriptor.EntityName} is tenant-owned (scope T); it has no global rows.")
            : !global && !descriptor.AllowsTenantRows
                ? Error.Forbidden(StaticDataErrorCodes.ScopeNotAllowed, $"{descriptor.EntityName} is platform-global (scope G); only platform administrators change it.")
                : null;

    private Error? CheckWrite<TEntity, TInput>(StaticDataDescriptor descriptor, TInput input)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        var errors = input.Validate();
        if (errors.Count == 0)
        {
            errors = ValidateRecord<TEntity, TInput>(descriptor, input);
        }

        return errors.Count > 0 ? Error.Validation(ErrorCodes.ValidationFailed, "The static data record is not valid.", errors) : null;
    }

    /// <summary>A module's own rules across a table's fields (IStaticRecordValidator), checked on a probe built from the input.</summary>
    private IReadOnlyDictionary<string, string[]> ValidateRecord<TEntity, TInput>(StaticDataDescriptor descriptor, TInput input)
        where TEntity : StaticRecord<TInput>, new()
        where TInput : StaticRecordInput
    {
        var applicable = validators.Where(v => v.Schema == descriptor.Schema && v.Table == descriptor.Table).ToList();
        if (applicable.Count == 0 || input.Validate().Count > 0)
        {
            return new Dictionary<string, string[]>();
        }

        var probe = new TEntity();
        probe.ApplyCommon(input);
        probe.ApplyFields(input, protector);
        return applicable.SelectMany(v => v.Validate(probe)).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.SelectMany(e => e.Value).ToArray());
    }

    /// <summary>The module context for tenant rows (shared with the request), or a new platform administrator context for global rows.</summary>
    private ContextLease Lease(bool global) => global ? new(contexts.CreatePlatformAdmin(), owned: true) : new(tenantDb, owned: false);

    private static IQueryable<TEntity> Scoped<TEntity>(IQueryable<TEntity> rows, bool global)
        where TEntity : StaticRecord =>
        global ? rows.Where(r => r.TenantId == null) : rows.Where(r => r.TenantId != null);

    private static IQueryable<TEntity> InForce<TEntity>(IQueryable<TEntity> rows, StaticDataDescriptor descriptor, DateOnly asAt)
        where TEntity : StaticRecord
    {
        var statuses = descriptor.Lifecycle.InForce;
        return rows.AsNoTracking().Where(r => statuses.Contains(r.RecordStatus) && r.EffectiveFrom <= asAt && (r.EffectiveTo == null || r.EffectiveTo > asAt));
    }

    /// <summary>Tenant rows override the global baseline for the same code (scope GT, SDM-008).</summary>
    private static IEnumerable<TEntity> Overlay<TEntity>(IEnumerable<TEntity> rows)
        where TEntity : StaticRecord =>
        rows.GroupBy(r => r.Code).Select(g => g.FirstOrDefault(r => r.TenantId is not null) ?? g.First()).OrderBy(r => r.Code, StringComparer.Ordinal);

    private static async Task<IDbContextTransaction> ReadOnly(TDbContext db, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        return transaction;
    }

    /// <summary>Before and after values for the audit trail (SDM-014), left out for personal and highly sensitive records.</summary>
    private static Dictionary<string, string?> AuditDetails(StaticDataDescriptor descriptor, StaticRecord entity, string? before, bool global)
    {
        var details = new Dictionary<string, string?>
        {
            ["code"] = entity.Code,
            ["version"] = entity.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = entity.RecordStatus,
            ["global"] = global.ToString(),
            ["tier"] = descriptor.Tier.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (entity.RecordClass is not ("PERSONAL" or "HIGHLY_SENSITIVE"))
        {
            details["before"] = before;
            details["after"] = Snapshot(entity);
        }

        return details;
    }

    private static string Snapshot(StaticRecord entity) => JsonSerializer.Serialize(entity, entity.GetType(), ModuleDbContext.EventJson);

    private static Error InvalidTransition(string message) => Error.Conflict(ErrorCodes.InvalidStateTransition, message);

    private static string DescribeDatabaseError(PostgresException postgres) => postgres.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation => "Duplicates or overlaps an existing record.",
        PostgresErrorCodes.CheckViolation => $"Breaks the rule {postgres.ConstraintName}.",
        PostgresErrorCodes.NotNullViolation => "A required value is missing.",
        PostgresErrorCodes.StringDataRightTruncation => "A value is too long.",
        _ => "The database rejected the line.",
    };

    /// <summary>Disposes the context only when this service created it.</summary>
    private readonly struct ContextLease(TDbContext context, bool owned) : IAsyncDisposable
    {
        public TDbContext Context { get; } = context;

        public ValueTask DisposeAsync() => owned ? Context.DisposeAsync() : ValueTask.CompletedTask;
    }
}
