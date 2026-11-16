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
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Assist.Application;

/// <summary>
/// The questionnaire (AI Assist SRS §5A.2). The country and the lines choose the overlays; the rest is recorded as given for the steps that use it (tailoring in
/// 7.8, the reviewer). All of it is business configuration (AIA-088).
/// </summary>
public sealed record SetupAnswers(string Country, IReadOnlyList<string> Lines, IReadOnlyList<string>? Currencies = null, string? TimeZone = null,
    IReadOnlyList<string>? Products = null, IReadOnlyList<string>? Channels = null, IReadOnlyList<string>? PaymentMethods = null, IReadOnlyList<string>? TeamRoles = null,
    IReadOnlyList<string>? Languages = null)
{
    private static readonly string[] KnownLines = ["LIFE", "GENERAL", "HEALTH"];

    public Result Check() =>
        Country is not { Length: 2 } || !Country.All(char.IsAsciiLetter) ? Error.Validation(ErrorCodes.ValidationFailed, "country is the tenant's country, two letters.")
        : Lines is not { Count: > 0 } || Lines.Any(l => !KnownLines.Contains(l, StringComparer.OrdinalIgnoreCase))
            ? Error.Validation(ErrorCodes.ValidationFailed, "lines are one or more of LIFE, GENERAL and HEALTH.")
            : Result.Success();
}

/// <summary>One drafted value, before it is stored.</summary>
public sealed record DraftValue(string Table, string Code, string Class, bool Regulated, string Flag, string? Values, Guid? SourcePackId, int? SourceLineNo, string? MissingReason);

/// <summary>A pack version as the assembly uses it: its rows, and whether it is approved for this tenant.</summary>
public sealed record AssemblyLayer(StarterPack Pack, IReadOnlyList<StarterPackValue> Rows, bool ApprovedForTenant);

/// <summary>
/// The deterministic assembly (AIA-082, AIA-087): the base pack, then the country's overlay (after the regional one it builds on), then each line's; a later
/// layer's row replaces an earlier one with the same table and code. No value is computed: each is copied from its pack, or left Missing.
/// <list type="bullet">
/// <item>A regulated row from a version not approved for this tenant is Missing (NOT_APPROVED, ASSIST_PACK_NOT_APPROVED), its value not filled. In such a
/// version, a FINANCE or COMPLIANCE row is regulated whatever its mark says, as nobody has confirmed the mark (D-20); and a row regulated in a lower layer
/// stays regulated, so an overlay can't unmark what the base marks.</item>
/// <item>A regulated row the pack leaves empty (a null or empty value) is Missing (NO_VALUE): only a reviewed overlay or the tenant can give it.</item>
/// <item>Anything else is Pack, copied as it is.</item>
/// </list>
/// </summary>
public static class SetupAssembly
{
    public static IReadOnlyList<DraftValue> Assemble(IReadOnlyList<AssemblyLayer> layers)
    {
        var rows = new Dictionary<(string, string), (AssemblyLayer Layer, StarterPackValue Row, bool Regulated)>();
        var order = new List<(string, string)>();
        foreach (var layer in layers)
        {
            foreach (var row in layer.Rows.OrderBy(r => r.LineNo))
            {
                var key = (row.TableName, row.RowCode);
                var below = rows.TryGetValue(key, out var earlier) && earlier.Regulated;
                if (!rows.ContainsKey(key))
                {
                    order.Add(key);
                }

                var regulated = row.Regulated || below || (!layer.ApprovedForTenant && row.Class is "FINANCE" or "COMPLIANCE");
                rows[key] = (layer, row, regulated);
            }
        }

        return [.. order.Select(key =>
        {
            var (layer, row, regulated) = rows[key];
            var missing = !regulated ? null : !layer.ApprovedForTenant ? MissingReasons.NotApproved : Empty(row.Values) ? MissingReasons.NoValue : null;
            return new DraftValue(row.TableName, row.RowCode, row.Class, regulated, missing is null ? ItemFlags.Pack : ItemFlags.Missing, missing is null ? row.Values : null,
                layer.Pack.Id, row.LineNo, missing);
        })];
    }

    /// <summary>Whether a row's values leave something out: no values, or any value null or an empty text.</summary>
    public static bool Empty(string values)
    {
        using var document = JsonDocument.Parse(values);
        var root = document.RootElement;
        return root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()
            || root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null || (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length == 0));
    }

    /// <summary>
    /// The versions a tenant's draft uses, in layer order: for each pack, the newest approved for every tenant, else the newest the tenant approved for itself,
    /// else the newest it may read (whose regulated rows are then Missing).
    /// </summary>
    public static IReadOnlyList<StarterPack> Choose(IReadOnlyList<StarterPack> readable, SetupAnswers answers, Guid tenant)
    {
        StarterPack? Best(Func<StarterPack, bool> which)
        {
            var candidates = readable.Where(which).ToList();
            return candidates.Where(p => p.Status == PackStatuses.Approved && p.Scope == PackScopes.AllTenants).MaxBy(p => p.Version)
                ?? candidates.Where(p => p.Status == PackStatuses.Approved && p.TenantId == tenant).MaxBy(p => p.Version)
                ?? candidates.MaxBy(p => (p.TenantId == tenant ? 1 : 0, p.Version));
        }

        var chosen = new List<StarterPack>();
        if (Best(p => p.Kind == PackKinds.Base) is { } basePack)
        {
            chosen.Add(basePack);
        }

        if (Best(p => p.Kind == PackKinds.Country && string.Equals(p.Country, answers.Country, StringComparison.OrdinalIgnoreCase)) is { } country)
        {
            if (country.BuildsOn is { } region && Best(p => p.PackCode == region) is { } regional)
            {
                chosen.Add(regional);
            }

            chosen.Add(country);
        }

        foreach (var line in answers.Lines.Select(l => l.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal))
        {
            chosen.AddRange(readable.Where(p => p.Kind == PackKinds.Line && p.Line == line).Select(p => p.PackCode).Distinct().Order(StringComparer.Ordinal)
                .Select(code => Best(p => p.PackCode == code)).OfType<StarterPack>());
        }

        return chosen;
    }

    public static bool ApprovedFor(StarterPack pack, Guid tenant) =>
        pack.Status == PackStatuses.Approved && (pack.Scope == PackScopes.AllTenants || pack.TenantId == tenant);
}

/// <param name="InDraft">False once the answers no longer bring the row: kept, rejected, never decided again.</param>
public sealed record SetupItemView(Guid Id, string Table, string Code, string Class, bool Regulated, string Flag, JsonElement? Values, Guid? SourcePackId, int? SourceLineNo,
    string? MissingReason, string? Decision, string? DecidedBy, string? DecisionNote, int AnswersVersion, bool InDraft, long RowVersion)
{
    public static SetupItemView From(SetupItem i) => new(i.Id, i.TableName, i.RowCode, i.Class, i.Regulated, i.Flag,
        i.Values is null ? null : JsonDocument.Parse(i.Values).RootElement.Clone(), i.SourcePackId, i.SourceLineNo, i.MissingReason, i.Decision, i.DecidedBy, i.DecisionNote,
        i.AnswersVersion, i.InDraft, i.RowVersion);
}

/// <summary>A run, its answers, its packs and all its items; the counts are of the items in the draft: how many, how many Missing, how many because an overlay isn't approved.</summary>
public sealed record SetupRunView(Guid Id, string Status, string StartedBy, int AnswersVersion, JsonElement Answers, IReadOnlyList<Guid> PackIds, DateTimeOffset AssembledAt,
    int Items, int Missing, int NotApproved, IReadOnlyList<SetupItemView> Values, long RowVersion);

public interface ISetupRuns
{
    void Add(SetupRun run);

    void Add(SetupAnswer answer);

    void Add(SetupItem item);

    Task<SetupRun?> Run(Guid id, CancellationToken cancellationToken);

    Task<SetupAnswer?> Answers(Guid runId, int version, CancellationToken cancellationToken);

    Task<IReadOnlyList<SetupItem>> Items(Guid runId, CancellationToken cancellationToken);

    Task<SetupItem?> Item(Guid runId, Guid itemId, CancellationToken cancellationToken);
}

public sealed record StartTenantSetup(SetupAnswers Answers) : ICommand<SetupRunView>;

public sealed record AnswerTenantSetup(Guid Id, SetupAnswers Answers, long ExpectedVersion) : ICommand<SetupRunView>;

public sealed record DecideSetupItem(Guid Id, Guid ItemId, string Decision, string? Note, long ExpectedVersion) : ICommand<SetupItemView>;

public sealed record GetTenantSetup(Guid Id) : IQuery<SetupRunView>;

/// <summary>
/// AI-3's pack-only path (AIA-090): the draft assembled from the packs, with no model, so it needs the function but not the AI_ASSIST entitlement. Only while the
/// tenant is onboarding (AIA-086). AI tailoring and document mapping join in 7.8.
/// </summary>
internal sealed class TenantSetupHandlers(ISetupRuns runs, IStarterPacks packs, IExecutionContext execution, ITenantContext tenant, IClock clock, IAuditTrail audit,
    Tenancy.Contracts.ITenantStatusQuery tenants)
    : ICommandHandler<StartTenantSetup, SetupRunView>, ICommandHandler<AnswerTenantSetup, SetupRunView>, ICommandHandler<DecideSetupItem, SetupItemView>,
        IQueryHandler<GetTenantSetup, SetupRunView>
{
    public async Task<Result<SetupRunView>> Handle(StartTenantSetup command, CancellationToken cancellationToken)
    {
        if (await Refusal(command.Answers, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (packIds, drafted) = await Draft(command.Answers, cancellationToken);
        var run = SetupRun.Start(execution.ActorId, packIds, clock.UtcNow);
        runs.Add(run);
        runs.Add(SetupAnswer.For(run.Id, 1, Serialised(command.Answers)));
        var items = drafted.Select(d => SetupItem.Draft(run.Id, 1, d.Table, d.Code, d.Class, d.Regulated, d.Flag, d.Values, d.SourcePackId, d.SourceLineNo, d.MissingReason)).ToList();
        items.ForEach(runs.Add);
        Audit("StartTenantSetup", run);
        return View(run, command.Answers, items);
    }

    public async Task<Result<SetupRunView>> Handle(AnswerTenantSetup command, CancellationToken cancellationToken)
    {
        if (await Refusal(command.Answers, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await runs.Run(command.Id, cancellationToken) is not { } run)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        if (run.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The setup has changed since it was read. Read it again.");
        }

        var (packIds, drafted) = await Draft(command.Answers, cancellationToken);
        var reanswered = run.Reanswer(packIds, clock.UtcNow);
        if (reanswered.IsFailure)
        {
            return reanswered.Error;
        }

        runs.Add(SetupAnswer.For(run.Id, run.AnswersVersion, Serialised(command.Answers)));
        // AIA-081: only what the new answers change is re-drafted; an unchanged value keeps its decision. A value the answers no longer bring leaves the draft.
        var all = await runs.Items(run.Id, cancellationToken);
        var existing = all.ToDictionary(i => (i.TableName, i.RowCode));
        var items = new List<SetupItem>(all);
        foreach (var d in drafted)
        {
            if (existing.Remove((d.Table, d.Code), out var item))
            {
                if (!item.InDraft || !item.Same(d.Flag, d.Values, d.SourcePackId, d.SourceLineNo) || item.Regulated != d.Regulated)
                {
                    item.Redraft(run.AnswersVersion, d.Class, d.Regulated, d.Flag, d.Values, d.SourcePackId, d.SourceLineNo, d.MissingReason);
                }
            }
            else
            {
                var added = SetupItem.Draft(run.Id, run.AnswersVersion, d.Table, d.Code, d.Class, d.Regulated, d.Flag, d.Values, d.SourcePackId, d.SourceLineNo, d.MissingReason);
                runs.Add(added);
                items.Add(added);
            }
        }

        foreach (var gone in existing.Values.Where(i => i.InDraft))
        {
            gone.LeaveDraft(execution.ActorId, clock.UtcNow);
        }

        Audit("AnswerTenantSetup", run);
        return View(run, command.Answers, items);
    }

    public async Task<Result<SetupItemView>> Handle(DecideSetupItem command, CancellationToken cancellationToken)
    {
        if (await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly)
        {
            return Error.Conflict(PackErrorCodes.TenantLive, "The setup wizard runs only while the tenant is onboarding.");
        }

        if (await runs.Run(command.Id, cancellationToken) is not { } run || await runs.Item(run.Id, command.ItemId, cancellationToken) is not { } item)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup or its value was not found.");
        }

        if (run.Status != SetupStatuses.Draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only a draft setup's values are decided.");
        }

        if (item.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The value has changed since it was read. Read it again.");
        }

        var decided = item.Decide(command.Decision, command.Note, execution.ActorId, clock.UtcNow);
        if (decided.IsFailure)
        {
            return decided.Error;
        }

        audit.Record(new AuditEntry("ASSIST", "DecideSetupItem", nameof(SetupItem), item.Id.ToString(), new Dictionary<string, string?>
        {
            ["run"] = run.Id.ToString(), ["table"] = item.TableName, ["code"] = item.RowCode, ["decision"] = item.Decision, ["flag"] = item.Flag,
        }));
        return SetupItemView.From(item);
    }

    public async Task<Result<SetupRunView>> Handle(GetTenantSetup query, CancellationToken cancellationToken)
    {
        if (await runs.Run(query.Id, cancellationToken) is not { } run || await runs.Answers(run.Id, run.AnswersVersion, cancellationToken) is not { } answers)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The setup was not found.");
        }

        return View(run, JsonSerializer.Deserialize<SetupAnswers>(answers.Answers, Json)!, await runs.Items(run.Id, cancellationToken));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Serialised(SetupAnswers answers) => JsonSerializer.Serialize(answers, Json);

    /// <summary>The answers' own check, then AIA-086: a tenant still onboarding only.</summary>
    private async Task<Error?> Refusal(SetupAnswers answers, CancellationToken cancellationToken)
    {
        if (answers.Check() is { IsFailure: true } bad)
        {
            return bad.Error;
        }

        return await tenants.UserAccess(tenant.RequireTenantId(), cancellationToken) != Tenancy.Contracts.TenantUserAccess.ConfigurationOnly
            ? Error.Conflict(PackErrorCodes.TenantLive, "The setup wizard runs only while the tenant is onboarding.")
            : null;
    }

    private async Task<(IReadOnlyList<Guid> PackIds, IReadOnlyList<DraftValue> Values)> Draft(SetupAnswers answers, CancellationToken cancellationToken)
    {
        var current = tenant.RequireTenantId();
        var chosen = SetupAssembly.Choose(await packs.List(null, cancellationToken), answers, current);
        var layers = new List<AssemblyLayer>();
        foreach (var pack in chosen)
        {
            layers.Add(new AssemblyLayer(pack, await packs.Rows(pack.Id, cancellationToken), SetupAssembly.ApprovedFor(pack, current)));
        }

        return ([.. chosen.Select(p => p.Id)], SetupAssembly.Assemble(layers));
    }

    private void Audit(string operation, SetupRun run) =>
        audit.Record(new AuditEntry("ASSIST", operation, nameof(SetupRun), run.Id.ToString(), new Dictionary<string, string?>
        {
            ["answersVersion"] = run.AnswersVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ["packs"] = string.Join(",", run.PackIds),
        }));

    private static SetupRunView View(SetupRun run, SetupAnswers answers, IReadOnlyList<SetupItem> items) =>
        new(run.Id, run.Status, run.StartedBy, run.AnswersVersion, JsonSerializer.SerializeToElement(answers, Json), run.PackIds, run.AssembledAt, items.Count(i => i.InDraft),
            items.Count(i => i.InDraft && i.Flag == ItemFlags.Missing), items.Count(i => i.InDraft && i.MissingReason == MissingReasons.NotApproved),
            // A value this command changed is saved at its next version, which is what a client's If-Match must name.
            [.. items.OrderBy(i => i.TableName, StringComparer.Ordinal).ThenBy(i => i.RowCode, StringComparer.Ordinal)
                .Select(i => SetupItemView.From(i) with { RowVersion = i.RowVersion + (i.HasChanges ? 1 : 0) })], run.RowVersion);
}
