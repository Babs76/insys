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

/// <summary>The questionnaire's answers by the names the API gives them: what a pack row's `tailor` may name as its source (OQ-187 (a)).</summary>
public static class SetupAnswerFields
{
    public static readonly string[] All = ["country", "lines", "currencies", "timeZone", "products", "channels", "paymentMethods", "teamRoles", "languages"];

    /// <summary>The values an answer holds, as text; an answer not given holds none.</summary>
    public static IReadOnlyList<string> Values(SetupAnswers answers, string field) => field switch
    {
        "country" => [answers.Country],
        "lines" => answers.Lines,
        "currencies" => answers.Currencies ?? [],
        "timeZone" => answers.TimeZone is { } zone ? [zone] : [],
        "products" => answers.Products ?? [],
        "channels" => answers.Channels ?? [],
        "paymentMethods" => answers.PaymentMethods ?? [],
        "teamRoles" => answers.TeamRoles ?? [],
        "languages" => answers.Languages ?? [],
        _ => [],
    };
}

/// <summary>One drafted value, before it is stored.</summary>
public sealed record DraftValue(string Table, string Code, string Class, bool Regulated, string Flag, string? Values, Guid? SourcePackId, int? SourceLineNo, string? MissingReason);

/// <summary>A pack version as the assembly uses it: its rows, whether it is approved for this tenant, and whether it only fills values still empty.</summary>
/// <param name="Fill">The tenant's own approved version (Ade, 16 November, OQ-181): applied last, it fills only values still empty, never replacing one.</param>
public sealed record AssemblyLayer(StarterPack Pack, IReadOnlyList<StarterPackValue> Rows, bool ApprovedForTenant, bool Fill = false);

/// <summary>A version the draft uses, and whether as a fill layer (the tenant's own approved, last).</summary>
public sealed record ChosenPack(StarterPack Pack, bool Fill);

/// <summary>
/// The deterministic assembly (AIA-082, AIA-087; layers and merging decided by Ade, 16 November, OQ-181). No value is computed: each is copied from a pack,
/// or left empty.
/// <list type="bullet">
/// <item>The layers are the base, then each line, then the country (after the regional overlay it builds on), each the platform's version; then the tenant's
/// own approved versions of the same packs, last.</item>
/// <item>A later layer's row merges into the earlier one with the same table and code, field by field. A null in a row means "no change", never "set to
/// null", so a line's empty value never erases what another layer gives.</item>
/// <item>A regulated field from a version not approved for this tenant is never used (NOT_APPROVED, ASSIST_PACK_NOT_APPROVED): the field is left empty,
/// even where an earlier layer gave it. In such a version a FINANCE or COMPLIANCE row is regulated whatever its mark (D-20), and a row regulated in a lower
/// layer stays regulated.</item>
/// <item>The tenant's own layers only fill fields still empty, so they never replace a value, a regulated one from a platform-approved version least of
/// all. They may add rows no other layer has. A tenant's draft fills as a platform draft does: never with a regulated value.</item>
/// <item>A regulated row with a field still empty (a null or empty text) is Missing: NOT_APPROVED if an unapproved version's value was left out, else
/// NO_VALUE. Anything else is Pack.</item>
/// </list>
/// </summary>
public static class SetupAssembly
{
    private sealed class Draft(string @class)
    {
        public string Class { get; set; } = @class;
        public bool Regulated { get; set; }
        public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Fields { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = [];
        public HashSet<string> NotApproved { get; } = new(StringComparer.Ordinal);
        public Guid SourcePackId { get; set; }
        public int SourceLineNo { get; set; }

        public bool IsEmpty(string field) => Fields[field] is null || (Fields[field] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0);
    }

    /// <summary>
    /// A layer names a field with no value, and changes nothing. Why it is still empty: a regulated value an unapproved version leaves empty is NOT_APPROVED;
    /// once an approved version names it empty too, it is NO_VALUE, as no approved value exists.
    /// </summary>
    private static void Unset(Draft draft, string field, AssemblyLayer layer)
    {
        if (!draft.Fields.ContainsKey(field) || !draft.IsEmpty(field))
        {
            return;
        }

        if (layer.ApprovedForTenant)
        {
            draft.NotApproved.Remove(field);
        }
        else if (draft.Regulated)
        {
            draft.NotApproved.Add(field);
        }
    }

    public static IReadOnlyList<DraftValue> Assemble(IReadOnlyList<AssemblyLayer> layers)
    {
        var rows = new Dictionary<(string, string), Draft>();
        var order = new List<(string, string)>();
        foreach (var layer in layers)
        {
            foreach (var row in layer.Rows.OrderBy(r => r.LineNo))
            {
                var key = (row.TableName, row.RowCode);
                var created = !rows.TryGetValue(key, out var draft);
                if (created)
                {
                    rows[key] = draft = new Draft(row.Class);
                    order.Add(key);
                }

                // Marks only ever tighten: a row regulated below stays so, and an unapproved version's FINANCE or COMPLIANCE row is regulated (D-20).
                draft!.Regulated = draft.Regulated || row.Regulated || (!layer.ApprovedForTenant && row.Class is "FINANCE" or "COMPLIANCE");
                if (!layer.Fill)
                {
                    draft.Class = row.Class;
                }

                using var document = JsonDocument.Parse(row.Values);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var contributed = false;
                foreach (var field in document.RootElement.EnumerateObject())
                {
                    // "No change": a null adds nothing, unless this layer brings the row, whose nulls mark the values still to be given.
                    if (field.Value.ValueKind == JsonValueKind.Null && !created)
                    {
                        Unset(draft, field.Name, layer);
                        continue;
                    }

                    if (!draft.Fields.ContainsKey(field.Name))
                    {
                        draft.Fields[field.Name] = null;
                        draft.Order.Add(field.Name);
                    }

                    if (field.Value.ValueKind == JsonValueKind.Null)
                    {
                        Unset(draft, field.Name, layer);
                        continue;
                    }

                    if (layer.Fill && !draft.IsEmpty(field.Name))
                    {
                        continue; // never replaces a value
                    }

                    if (draft.Regulated && !layer.ApprovedForTenant)
                    {
                        draft.Fields[field.Name] = null;
                        draft.NotApproved.Add(field.Name);
                        contributed = true;
                        continue;
                    }

                    draft.Fields[field.Name] = System.Text.Json.Nodes.JsonNode.Parse(field.Value.GetRawText());
                    draft.NotApproved.Remove(field.Name);
                    contributed = true;
                }

                if (contributed || draft.SourcePackId == Guid.Empty)
                {
                    (draft.SourcePackId, draft.SourceLineNo) = (layer.Pack.Id, row.LineNo);
                }
            }
        }

        return [.. order.Select(key =>
        {
            var draft = rows[key];
            var empty = draft.Order.Where(draft.IsEmpty).ToList();
            var missing = !draft.Regulated || (empty.Count > 0 || draft.Fields.Count == 0) is false ? null
                : empty.Any(draft.NotApproved.Contains) ? MissingReasons.NotApproved : MissingReasons.NoValue;
            var values = new System.Text.Json.Nodes.JsonObject();
            foreach (var field in draft.Order)
            {
                values[field] = draft.Fields[field]?.DeepClone();
            }

            return new DraftValue(key.Item1, key.Item2, draft.Class, draft.Regulated, missing is null ? ItemFlags.Pack : ItemFlags.Missing,
                missing is null ? values.ToJsonString() : null, draft.SourcePackId, draft.SourceLineNo, missing);
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
    /// The versions a tenant's draft uses, in layer order (Ade, 16 November, OQ-181): the base, each line, the regional overlay and the country, each the
    /// platform's newest approved version, else its newest draft (whose regulated values are then left empty); then, as fill layers, the tenant's own newest
    /// approved version of each of those packs, else its newest draft, whose regulated values are never used either (NOT_APPROVED).
    /// </summary>
    public static IReadOnlyList<ChosenPack> Choose(IReadOnlyList<StarterPack> readable, SetupAnswers answers, Guid tenant)
    {
        var codes = new List<string>();
        void Add(string? code)
        {
            if (code is not null && !codes.Contains(code, StringComparer.Ordinal))
            {
                codes.Add(code);
            }
        }

        Add(readable.Where(p => p.Kind == PackKinds.Base).Select(p => p.PackCode).Order(StringComparer.Ordinal).FirstOrDefault());
        foreach (var line in answers.Lines.Select(l => l.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal))
        {
            foreach (var code in readable.Where(p => p.Kind == PackKinds.Line && p.Line == line).Select(p => p.PackCode).Distinct().Order(StringComparer.Ordinal))
            {
                Add(code);
            }
        }

        var country = readable.Where(p => p.Kind == PackKinds.Country && string.Equals(p.Country, answers.Country, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PackCode).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
        if (country is not null && readable.Where(p => p.PackCode == country).Select(p => p.BuildsOn).FirstOrDefault(b => b is not null) is { } region)
        {
            Add(region);
        }

        Add(country);
        var chosen = new List<ChosenPack>();
        foreach (var code in codes)
        {
            var platform = readable.Where(p => p.PackCode == code && p.Scope == PackScopes.AllTenants).ToList();
            if ((platform.Where(p => p.Status == PackStatuses.Approved).MaxBy(p => p.Version) ?? platform.MaxBy(p => p.Version)) is { } layer)
            {
                chosen.Add(new ChosenPack(layer, Fill: false));
            }
        }

        foreach (var code in codes)
        {
            var mine = readable.Where(p => p.PackCode == code && p.TenantId == tenant && p.Scope != PackScopes.AllTenants).ToList();
            if ((mine.Where(p => p.Status == PackStatuses.Approved).MaxBy(p => p.Version) ?? mine.MaxBy(p => p.Version)) is { } own)
            {
                chosen.Add(new ChosenPack(own, Fill: true));
            }
        }

        return chosen;
    }

    /// <summary>The same values as JSON, whatever the order of their keys: a stored value comes back in jsonb's order.</summary>
    public static bool SameValues(string? a, string? b) =>
        a == b || (a is not null && b is not null && System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(a), System.Text.Json.Nodes.JsonNode.Parse(b)));

    public static bool ApprovedFor(StarterPack pack, Guid tenant) =>
        pack.Status == PackStatuses.Approved && (pack.Scope == PackScopes.AllTenants || pack.TenantId == tenant);
}

/// <param name="InDraft">False once the answers no longer bring the row: kept, rejected, never decided again.</param>
/// <param name="AiPassId">The AI pass that tailored or mapped the value (7.8); null for Pack and Missing.</param>
/// <param name="CitationDocumentId">A Mapped value's citation (AIA-083, AIA-084): its document, page and the exact words it comes from.</param>
/// <param name="CitedAnswer">A Tailored value's citation (AIA-084, OQ-187 (a)): the questionnaire answer, and the answer's value used.</param>
public sealed record SetupItemView(Guid Id, string Table, string Code, string Class, bool Regulated, string Flag, JsonElement? Values, Guid? SourcePackId, int? SourceLineNo,
    string? MissingReason, string? Decision, string? DecidedBy, string? DecisionNote, int AnswersVersion, bool InDraft, long RowVersion, Guid? AiPassId = null,
    Guid? CitationDocumentId = null, int? CitationPage = null, string? CitationQuote = null, string? CitedAnswer = null, string? CitedAnswerValue = null)
{
    public static SetupItemView From(SetupItem i) => new(i.Id, i.TableName, i.RowCode, i.Class, i.Regulated, i.Flag,
        i.Values is null ? null : JsonDocument.Parse(i.Values).RootElement.Clone(), i.SourcePackId, i.SourceLineNo, i.MissingReason, i.Decision, i.DecidedBy, i.DecisionNote,
        i.AnswersVersion, i.InDraft, i.RowVersion, i.AiRunId, i.CitationDocumentId, i.CitationPage, i.CitationQuote, i.CitedAnswer, i.CitedAnswerValue);
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

    void Add(SetupDocument document);

    void Add(SetupAiRun pass);

    void Add(SetupUnmapped line);

    /// <summary>The run's documents, removed ones included (their confirmations stay on record).</summary>
    Task<IReadOnlyList<SetupDocument>> Documents(Guid runId, CancellationToken cancellationToken);

    /// <summary>The run's AI passes, newest first.</summary>
    Task<IReadOnlyList<SetupAiRun>> AiRuns(Guid runId, CancellationToken cancellationToken);

    Task<SetupAiRun?> AiRun(Guid passId, CancellationToken cancellationToken);

    /// <summary>The tenant's passes still queued or running, oldest first.</summary>
    Task<IReadOnlyList<SetupAiRun>> OpenAiRuns(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<SetupUnmapped>> Unmapped(Guid runId, CancellationToken cancellationToken);
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
                // 7.8: a tailored value stays while the pack row is unchanged and its answer still holds the value it cited (AIA-081, OQ-187 (a)).
                if (item.Flag == ItemFlags.Tailored && item.InDraft && StillTailored(item, d, command.Answers))
                {
                    continue;
                }

                if (!item.InDraft || !item.Same(d.Flag, d.Values, d.SourcePackId, d.SourceLineNo, SetupAssembly.SameValues) || item.Regulated != d.Regulated)
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

        // A mapped row is the tenant's document's, not the packs': new answers don't take it out (AIA-081 re-drafts what the answers affect).
        foreach (var gone in existing.Values.Where(i => i.InDraft && i.Flag != ItemFlags.Mapped))
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

        run.ValueDecided();

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

    /// <summary>
    /// Whether a tailored value still stands on the new answers: same pack row, its values differing from the pack's in one column only, set to the value it cited,
    /// which the cited answer still holds.
    /// </summary>
    internal static bool StillTailored(SetupItem item, DraftValue pack, SetupAnswers answers)
    {
        if (pack.Flag != ItemFlags.Pack || pack.Regulated || item.SourcePackId != pack.SourcePackId || item.SourceLineNo != pack.SourceLineNo
            || item.CitedAnswer is not { } answer || item.CitedAnswerValue is not { } value || item.Values is null || pack.Values is null
            || !SetupAnswerFields.Values(answers, answer).Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        var tailored = System.Text.Json.Nodes.JsonNode.Parse(item.Values) as System.Text.Json.Nodes.JsonObject;
        var original = System.Text.Json.Nodes.JsonNode.Parse(pack.Values) as System.Text.Json.Nodes.JsonObject;
        if (tailored is null || original is null || tailored.Count != original.Count)
        {
            return false;
        }

        var changed = tailored.Where(p => !System.Text.Json.Nodes.JsonNode.DeepEquals(p.Value, original[p.Key])).ToList();
        return changed.Count == 1 && changed[0].Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s == value;
    }

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
        foreach (var (pack, fill) in chosen)
        {
            layers.Add(new AssemblyLayer(pack, await packs.Rows(pack.Id, cancellationToken), SetupAssembly.ApprovedFor(pack, current), fill));
        }

        return ([.. chosen.Select(c => c.Pack.Id)], SetupAssembly.Assemble(layers));
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
