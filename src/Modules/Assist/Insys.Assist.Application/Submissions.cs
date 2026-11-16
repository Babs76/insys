/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.StaticData.Contracts;

namespace Insys.Assist.Application;

/// <summary>Why a setup can't be submitted yet, or what its reviewer should see. Never a value: the table, the code and the field only.</summary>
/// <param name="Code">
/// UNDECIDED (a value in the draft not accepted or rejected), MISSING (a value in the draft is Missing), NOT_APPROVED (an accepted regulated value whose
/// overlay is no longer approved for the tenant), UNKNOWN_TABLE (no module owns the table), GLOBAL_TABLE (a platform-wide table, loaded by the platform),
/// LINE_REFUSED (the table refuses the value: the import's own checks), NOTHING_ACCEPTED; or, as a warning, REJECTED (a value the reviewer rejected, which
/// the setup leaves out).
/// </param>
public sealed record SetupFinding(string Code, string? Table, string? RowCode, string? Field, string Message);

/// <summary>
/// AIA-085's completeness check, deterministic and before approval: <see cref="Ready"/> when nothing blocks. <see cref="Tables"/> and <see cref="Values"/> are
/// what the SETUP change set would hold: one batch per table, in dependency order.
/// </summary>
public sealed record SetupCompletenessView(Guid Id, string Status, bool Ready, int Tables, int Values, IReadOnlyList<SetupFinding> Blockers, IReadOnlyList<SetupFinding> Warnings);

/// <param name="SetId">The SETUP change set, once submitted; its batches' change requests are under /static-data/change-requests.</param>
public sealed record SetupSubmissionView(Guid Id, string Status, int Attempt, Guid? SetId, string? SubmittedBy, DateTimeOffset? SubmittedAt, IReadOnlyList<SetupBatchView> Batches,
    long RowVersion);

public sealed record SetupBatchView(int Seq, string Table, Guid BatchId, Guid ChangeRequestId, int Lines, string Hash);

public sealed record CheckTenantSetup(Guid Id) : IQuery<SetupCompletenessView>;

/// <summary>Step one of submission: the completeness check must pass, then the values are locked (SUBMITTING). Only whoever ran the wizard submits.</summary>
public sealed record BeginSetupSubmission(Guid Id, long ExpectedVersion) : ICommand<SetupSubmissionView>, IVersionedCommand;

/// <summary>
/// Step two: each table's batch is imported and submitted to the set, in dependency order, then the set is requested. Resumable: run again after a failure,
/// it does nothing twice.
/// </summary>
public sealed record CompleteSetupSubmission(Guid Id) : ICommand<SetupSubmissionView>;

/// <summary>The submitter withdraws a submission that stopped part-way: the batches loaded are discarded, and the setup is a draft again.</summary>
public sealed record WithdrawSetupSubmission(Guid Id, long ExpectedVersion) : ICommand<SetupSubmissionView>, IVersionedCommand;

/// <summary>The SETUP change set (AIA-085, AIA-086). Only while the tenant is onboarding.</summary>
internal sealed class SetupSubmissionHandlers(ISetupRuns runs, IStarterPacks packs, ISetupImports imports, IExecutionContext execution, ITenantContext tenant, IClock clock,
    IAuditTrail audit, Tenancy.Contracts.ITenantStatusQuery tenants)
    : IQueryHandler<CheckTenantSetup, SetupCompletenessView>, ICommandHandler<BeginSetupSubmission, SetupSubmissionView>,
        ICommandHandler<CompleteSetupSubmission, SetupSubmissionView>, ICommandHandler<WithdrawSetupSubmission, SetupSubmissionView>
{
    public async Task<Result<SetupSubmissionView>> Handle(WithdrawSetupSubmission command, CancellationToken cancellationToken)
    {
        if (await runs.Run(command.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        if (run.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The setup has changed since it was read. Read it again.");
        }

        var setId = run.SetId;
        var withdrawn = run.WithdrawSubmission(execution.ActorId, clock.UtcNow);
        if (withdrawn.IsFailure)
        {
            return withdrawn.Error;
        }

        audit.Record(new AuditEntry("ASSIST", "WithdrawSetupSubmission", nameof(SetupRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["attempt"] = run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), ["set"] = setId?.ToString(),
        }));
        return View(run, []);
    }

    public async Task<Result<SetupCompletenessView>> Handle(CheckTenantSetup query, CancellationToken cancellationToken)
    {
        if (await runs.Run(query.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        return await Completeness(run, cancellationToken);
    }

    public async Task<Result<SetupSubmissionView>> Handle(BeginSetupSubmission command, CancellationToken cancellationToken)
    {
        if (await Onboarding(cancellationToken) is { } live)
        {
            return live;
        }

        if (await runs.Run(command.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        if (run.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The setup has changed since it was read. Read it again.");
        }

        // A retry of a submission that stopped part-way resumes it (step two); nothing is checked or locked again.
        if (run.Status == SetupStatuses.Submitting && run.SubmittedBy == execution.ActorId)
        {
            return View(run, []);
        }

        if (run.Status == SetupStatuses.Draft && run.StartedBy == execution.ActorId)
        {
            var completeness = await Completeness(run, cancellationToken);
            if (!completeness.Ready)
            {
                return Error.Validation(PackErrorCodes.SetupNotReady, $"The setup isn't ready: {completeness.Blockers.Count} blocker(s). Read its completeness check.",
                    completeness.Blockers.GroupBy(b => b.Table ?? "setup").ToDictionary(g => g.Key, g => g.Select(b => $"{b.Code}: {b.RowCode} {b.Field} {b.Message}".Replace("  ", " ", StringComparison.Ordinal)).ToArray()));
            }
        }

        var begun = run.BeginSubmission(execution.ActorId, clock.UtcNow);
        if (begun.IsFailure)
        {
            return begun.Error;
        }

        Audit("BeginSetupSubmission", run, null);
        return View(run, []);
    }

    public async Task<Result<SetupSubmissionView>> Handle(CompleteSetupSubmission command, CancellationToken cancellationToken)
    {
        if (await Onboarding(cancellationToken) is { } live)
        {
            return live;
        }

        if (await runs.Run(command.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        if (run.Status != SetupStatuses.Submitting || run.SetId is not { } setId || run.SubmittedBy != execution.ActorId)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The setup is {run.Status}; submission resumes only for whoever submitted it.");
        }

        var batches = new List<SetupBatchLoaded>();
        var seq = 0;
        foreach (var (table, lines) in Batches(run, await runs.Items(run.Id, cancellationToken)))
        {
            var loaded = await imports.ImportToSet(table, lines, SetupOrder.BatchId(run.Id, run.Attempt, table), setId, cancellationToken);
            if (loaded.IsFailure)
            {
                // The batches before this one stay loaded; submitting again resumes from here (AIA-086).
                return loaded.Error with { Message = $"{table}: {loaded.Error.Message} The submission stopped here; submit again to resume it." };
            }

            batches.Add(new SetupBatchLoaded(++seq, table, loaded.Value.EntityCode, loaded.Value.BatchId, loaded.Value.ChangeRequestId, loaded.Value.Lines, loaded.Value.Hash));
        }

        var submitted = run.Submitted(batches, clock.UtcNow);
        if (submitted.IsFailure)
        {
            return submitted.Error;
        }

        Audit("SubmitTenantSetup", run, batches.Count);
        return View(run, batches);
    }

    /// <summary>
    /// AIA-085's deterministic check. Every value in the draft decided and none Missing; every accepted regulated value from an overlay approved for the tenant
    /// now; every table owned by a module and open to tenant rows; and every accepted value as its table's import would take it, checked without saving.
    /// </summary>
    private async Task<SetupCompletenessView> Completeness(SetupRun run, CancellationToken cancellationToken)
    {
        var items = await runs.Items(run.Id, cancellationToken);
        var blockers = new List<SetupFinding>();
        var warnings = new List<SetupFinding>();
        var current = tenant.RequireTenantId();
        var approved = new Dictionary<Guid, bool>();
        foreach (var item in items.Where(i => i.InDraft).OrderBy(i => i.TableName, StringComparer.Ordinal).ThenBy(i => i.RowCode, StringComparer.Ordinal))
        {
            if (item.Flag == ItemFlags.Missing)
            {
                blockers.Add(new SetupFinding("MISSING", item.TableName, item.RowCode, null, item.MissingReason == MissingReasons.NotApproved
                    ? "A regulated value from an overlay not approved for this tenant: it can come only from an approved overlay (AIA-082)."
                    : "The pack leaves this regulated value empty: only a reviewed overlay or the tenant can give it."));
            }
            else if (item.Decision is null)
            {
                blockers.Add(new SetupFinding("UNDECIDED", item.TableName, item.RowCode, null, "The value is neither accepted nor rejected."));
            }
            else if (item.Decision == ItemDecisions.Rejected)
            {
                warnings.Add(new SetupFinding("REJECTED", item.TableName, item.RowCode, null, "Rejected by the reviewer: the setup leaves it out."));
            }
            else if (item.Regulated && item.SourcePackId is { } source)
            {
                if (!approved.TryGetValue(source, out var ok))
                {
                    ok = await packs.Get(source, cancellationToken) is { } pack && SetupAssembly.ApprovedFor(pack, current);
                    approved[source] = ok;
                }

                if (!ok)
                {
                    blockers.Add(new SetupFinding("NOT_APPROVED", item.TableName, item.RowCode, null,
                        "A regulated value whose overlay version is no longer approved for this tenant (AIA-082)."));
                }
            }
        }

        var batches = Batches(run, items);
        foreach (var (table, lines) in batches)
        {
            if (imports.Table(table) is not { } owned)
            {
                blockers.Add(new SetupFinding("UNKNOWN_TABLE", table, null, null, "No module owns this table."));
            }
            else if (!owned.TakesTenantRows)
            {
                blockers.Add(new SetupFinding("GLOBAL_TABLE", table, null, null, "A platform-wide table: its rows are loaded once, by the platform, when an overlay is approved."));
            }
            else
            {
                blockers.AddRange((await imports.Check(table, lines, cancellationToken))
                    .Select(e => new SetupFinding("LINE_REFUSED", table, e.Code, e.Field, e.Message)));
            }
        }

        if (batches.Count == 0)
        {
            blockers.Add(new SetupFinding("NOTHING_ACCEPTED", null, null, null, "No value is accepted, so there is nothing to submit."));
        }

        return new SetupCompletenessView(run.Id, run.Status, blockers.Count == 0, batches.Count, batches.Sum(b => b.Lines.Count), blockers, warnings);
    }

    /// <summary>The accepted values in the draft, one batch per table, in dependency order (<see cref="SetupOrder"/>); lines in code order.</summary>
    private static List<(string Table, IReadOnlyList<SetupLine> Lines)> Batches(SetupRun run, IReadOnlyList<SetupItem> items)
    {
        var reason = $"AI-3 tenant setup {run.Id:D}, attempt {(run.Attempt == 0 ? 1 : run.Attempt).ToString(System.Globalization.CultureInfo.InvariantCulture)} (AIA-086)";
        return [.. items.Where(i => i.InDraft && i.Decision == ItemDecisions.Accepted && i.Flag != ItemFlags.Missing && i.Values is not null)
            .GroupBy(i => i.TableName)
            .OrderBy(g => SetupOrder.Of(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<SetupLine>)[.. g.OrderBy(i => i.RowCode, StringComparer.Ordinal)
                .Select((i, n) => new SetupLine(n + 1, i.RowCode, JsonDocument.Parse(i.Values!).RootElement.Clone(), reason))]))];
    }

    private async Task<Error?> Onboarding(CancellationToken cancellationToken) =>
        await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly
            ? Error.Conflict(PackErrorCodes.TenantLive, "A setup is submitted only while the tenant is onboarding; a live tenant keeps per-table imports (AIA-086).")
            : null;

    private void Audit(string operation, SetupRun run, int? batches) =>
        audit.Record(new AuditEntry("ASSIST", operation, nameof(SetupRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["attempt"] = run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture), ["set"] = run.SetId?.ToString(),
            ["batches"] = batches?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }));

    private static SetupSubmissionView View(SetupRun run, IReadOnlyList<SetupBatchLoaded> batches) =>
        new(run.Id, run.Status, run.Attempt, run.SetId, run.SubmittedBy, run.SubmittedAt,
            [.. batches.Select(b => new SetupBatchView(b.Seq, b.Table, b.BatchId, b.ChangeRequestId, b.Lines, b.Hash))], run.RowVersion + (run.HasChanges ? 1 : 0));
}

/// <summary>The run follows its SETUP change set: APPLIED once in force, or a draft again if it wasn't approved (its drafts discarded by their modules).</summary>
public sealed class SetupChangeSetOutcomes(ISetupRuns runs, IAuditTrail audit) :
    IIntegrationEventHandler<SetupChangeSetApplied>, IIntegrationEventHandler<SetupChangeSetNotApproved>
{
    public async Task Handle(SetupChangeSetApplied applied, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await runs.Run(applied.SourceRef, cancellationToken) is { } run && run.Applied(applied.SetId))
        {
            audit.Record(new AuditEntry("ASSIST", "TenantSetupApplied", nameof(SetupRun), run.Id.ToString(),
                new Dictionary<string, string?> { ["set"] = applied.SetId.ToString(), ["checker"] = applied.CheckerId.ToString() }));
        }
    }

    public async Task Handle(SetupChangeSetNotApproved ended, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await runs.Run(ended.SourceRef, cancellationToken) is { } run && run.NotApproved(ended.SetId))
        {
            audit.Record(new AuditEntry("ASSIST", "TenantSetupNotApproved", nameof(SetupRun), run.Id.ToString(),
                new Dictionary<string, string?> { ["set"] = ended.SetId.ToString(), ["ending"] = ended.Ending }));
        }
    }
}
