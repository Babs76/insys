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
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.Assist.Contracts;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.ProductFactory.Contracts;

namespace Insys.Assist.Application;

/// <summary>A decision on one proposed value (AIA-019): ACCEPTED, EDITED with the person's value and why, or REJECTED.</summary>
public sealed record DraftItemDecision(string Decision, JsonElement? Value, string? Note);

/// <summary>Records a decision already checked by <see cref="ProductDraftReviewer"/>, and any write of the Draft it made.</summary>
/// <param name="WrittenToDraft">Whether the decision changed the Draft's own field, written by the reviewer as the user before this command.</param>
public sealed record DecideDraftItem(Guid RunId, Guid ItemId, string Decision, string? EditedValue, string? Note, long ExpectedVersion, bool WrittenToDraft)
    : ICommand<DraftItemView>;

/// <summary>One of the requester's run's items, with the run, for the reviewer's checks.</summary>
public sealed record GetDraftItemForReview(Guid RunId, Guid ItemId) : IQuery<DraftItemForReview>;

public sealed record DraftItemForReview(ProductDraftRun Run, DraftItem Item);

/// <summary>
/// AIA-019, as the configurator: accept, edit or reject each proposed value of their own run. The AI's proposal is never changed; an edit is recorded against it.
/// <list type="bullet">
/// <item>A version's own field (OQ-184 (a)) is written to the Draft as the user, through Product Factory's own edit (AIA-017), so the Draft holds what was
/// decided: the proposal when accepted, the person's value when edited, and the value of the version the Draft was copied from when rejected. Product Factory's
/// rules apply: a version past Validated refuses it.</item>
/// <item>Everything else (table rows, rate tables, rules, simulation cases) is recorded only: the configurator enters it in its own module, and the provenance
/// report shows it to the approver (AIA-020).</item>
/// <item>A person's value is checked as the field allows: the domain's coverage codes, the allowed codes, a name's length, a date not before the business date.</item>
/// </list>
/// Product Factory's write commits first and Assist's record second, each in its own transaction (one aggregate per transaction); a record refused after the write
/// leaves the Draft as decided, which the next decision or the provenance report's comparison shows.
/// </summary>
public sealed class ProductDraftReviewer(IDispatcher dispatcher, IProductDrafts products, IBusinessDate businessDate)
{
    private const int MaxValueLength = 20_000;

    public async Task<Result<DraftItemView>> Decide(Guid runId, Guid itemId, DraftItemDecision decision, long expectedVersion, CancellationToken cancellationToken)
    {
        var loaded = await dispatcher.Query(new GetDraftItemForReview(runId, itemId), cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (run, item) = (loaded.Value.Run, loaded.Value.Item);
        if (run.Status != ProductDraftStatuses.Drafted || run.DraftVersion is not { } version)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"The run is {run.Status}; its values are reviewed once it has drafted a version.");
        }

        if (item.RowVersion != expectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The value has changed since it was read. Read it again.");
        }

        string? edited = null;
        if (decision.Decision == DraftItemDecisions.Edited)
        {
            if (decision.Value is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, "An edit gives the value and why (AIA-019).");
            }

            edited = value.GetRawText();
            if (edited.Length > MaxValueLength)
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"A value is at most {MaxValueLength} characters of JSON.");
            }
        }

        // The domain's own rules first, on a copy, so nothing is written to the Draft for a decision the item would refuse.
        var precheck = DraftReviewRules.Check(item, decision.Decision, edited, decision.Note);
        if (precheck.IsFailure)
        {
            return precheck.Error;
        }

        var written = false;
        if (DraftReviewRules.IsVersionField(item))
        {
            var target = await products.Describe(run.ProductCode, cancellationToken);
            var draft = target is null ? null : await products.Version(run.ProductCode, version, cancellationToken);
            if (target is null || draft is null)
            {
                return Error.NotFound(ErrorCodes.NotFound, $"Version {version} of {run.ProductCode} was not found.");
            }

            var wanted = decision.Decision switch
            {
                DraftItemDecisions.Accepted => item.Value,
                DraftItemDecisions.Edited => edited,
                _ => null,
            };
            if (decision.Decision == DraftItemDecisions.Rejected)
            {
                if (item.FieldPath == DraftFields.EffectiveFrom)
                {
                    return Error.Validation(ErrorCodes.ValidationFailed,
                        "A version always has an effective date: to replace the proposed one, edit it with the date to use.");
                }

                var copiedFrom = version > 1 ? await products.Version(run.ProductCode, version - 1, cancellationToken) : null;
                if (copiedFrom is null)
                {
                    return Error.Conflict(ErrorCodes.InvalidStateTransition, "The version the Draft was copied from can't be read, so the field isn't put back: edit it instead.");
                }

                wanted = DraftReviewRules.FieldOf(copiedFrom, item.FieldPath);
            }

            // Checked only when it is written: a value the Draft already holds was checked when it was written.
            if (!DraftReviewRules.SameJson(DraftReviewRules.FieldOf(draft, item.FieldPath), wanted))
            {
                var checkedValue = DraftReviewRules.CheckVersionValue(item.FieldPath, wanted, target, businessDate.Today);
                if (checkedValue.IsFailure)
                {
                    return checkedValue.Error;
                }

                var (fields, effectiveFrom) = DraftReviewRules.Fields(item.FieldPath, wanted!);
                var outcome = await products.Edit(run.ProductCode, version, fields, effectiveFrom, cancellationToken);
                if (!outcome.Succeeded)
                {
                    return new Error(outcome.ErrorCode!, outcome.Message ?? "Product Factory refused the edit.", DraftReviewRules.KindOf(outcome.ErrorCode!));
                }

                written = true;
            }
        }
        else if (edited is not null && DraftReviewRules.CheckOtherValue(item.Kind, edited) is { IsFailure: true } other)
        {
            return other.Error;
        }

        return await dispatcher.Send(new DecideDraftItem(runId, itemId, decision.Decision, edited, decision.Note, expectedVersion, written), cancellationToken);
    }
}

/// <summary>The deterministic checks on a decision (AIA-019), shared by the reviewer and the record.</summary>
public static class DraftReviewRules
{
    private static readonly HashSet<string> VersionFields = new(StringComparer.Ordinal)
    {
        DraftFields.Name, DraftFields.Coverages, DraftFields.EffectiveFrom, DraftFields.CoverStartsOn, DraftFields.ReinstatementArrears,
    };

    public static bool IsVersionField(DraftItem item) => item.Kind == DraftItemKinds.Value && VersionFields.Contains(item.FieldPath);

    /// <summary>The item's own rules (<see cref="DraftItem.Decide"/>), checked without changing it.</summary>
    public static Result Check(DraftItem item, string decision, string? edited, string? note)
    {
        if (!DraftItemDecisions.All.Contains(decision))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "decision is ACCEPTED, EDITED or REJECTED.");
        }

        if (item.CheckResult != "PASSED")
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, $"This proposal was dropped by INSYS's checks ({item.CheckReason}), so it isn't decided: enter the value in Product Factory.");
        }

        if (item.Flag == DraftItemFlags.Missing && decision != DraftItemDecisions.Edited)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A Missing value is filled by an edit: there is no proposal to accept or reject.");
        }

        if (decision == DraftItemDecisions.Edited && (edited is null || string.IsNullOrWhiteSpace(note)))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "An edit gives the value and why (AIA-019).");
        }

        return decision != DraftItemDecisions.Edited && edited is not null
            ? Error.Validation(ErrorCodes.ValidationFailed, "Only an edit gives a value.")
            : note is { Length: > 500 } ? Error.Validation(ErrorCodes.ValidationFailed, "note is at most 500 characters.") : Result.Success();
    }

    /// <summary>A version's own field as JSON text, as the Draft or a version holds it.</summary>
    public static string? FieldOf(ProductDraftVersion version, string path) => path switch
    {
        DraftFields.Name => JsonSerializer.Serialize(version.Name),
        DraftFields.Coverages => JsonSerializer.Serialize(version.Coverages),
        DraftFields.EffectiveFrom => JsonSerializer.Serialize(version.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DraftFields.CoverStartsOn => JsonSerializer.Serialize(version.CoverStartsOn),
        DraftFields.ReinstatementArrears => JsonSerializer.Serialize(version.ReinstatementArrears),
        _ => null,
    };

    /// <summary>
    /// A version field's value as the field allows (AIA-012): a name of 1 to 100 characters; coverages, one or more of the domain's codes; the allowed codes;
    /// an effective date as YYYY-MM-DD, not before the business date.
    /// </summary>
    public static Result CheckVersionValue(string path, string? json, ProductDraftTarget target, DateOnly today)
    {
        JsonNode? node;
        try
        {
            node = json is null ? null : JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }

        string? Text() => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return path switch
        {
            DraftFields.Name when Text() is { } name && !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100 => Result.Success(),
            DraftFields.Name => Invalid("version.name is text of 1 to 100 characters."),
            DraftFields.Coverages when node is JsonArray codes && codes.Count > 0
                && codes.All(c => c is JsonValue cv && cv.TryGetValue<string>(out var code) && target.CoverageCodes.Contains(code, StringComparer.Ordinal))
                && codes.Select(c => c!.ToJsonString()).Distinct(StringComparer.Ordinal).Count() == codes.Count => Result.Success(),
            DraftFields.Coverages => Invalid($"version.coverages is one or more different codes of {target.Domain}'s coverages: {string.Join(", ", target.CoverageCodes)}."),
            DraftFields.CoverStartsOn when Text() is "Issue" or "FirstPayment" => Result.Success(),
            DraftFields.CoverStartsOn => Invalid("version.coverStartsOn is Issue or FirstPayment."),
            DraftFields.ReinstatementArrears when Text() is "None" or "Required" => Result.Success(),
            DraftFields.ReinstatementArrears => Invalid("version.reinstatementArrears is None or Required."),
            DraftFields.EffectiveFrom when DateOnly.TryParseExact(Text(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day >= today
                => Result.Success(),
            DraftFields.EffectiveFrom => Invalid($"version.effectiveFrom is a date as YYYY-MM-DD, not before {today:yyyy-MM-dd}."),
            _ => Invalid($"{path} isn't a version field."),
        };
    }

    /// <summary>A person's value for an item Product Factory isn't written for: the shape its kind takes, checked as data only.</summary>
    public static Result CheckOtherValue(string kind, string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Invalid("value is JSON.");
        }

        return kind switch
        {
            DraftItemKinds.RateTable when node is JsonArray rows && rows.Count > 0 && rows.All(r => r is JsonArray cells && cells.All(c => c is JsonValue)) => Result.Success(),
            DraftItemKinds.RateTable => Invalid("A rate table is its rows of cells: an array of arrays of text or numbers."),
            DraftItemKinds.Rule when node is JsonValue rule && rule.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) => Result.Success(),
            DraftItemKinds.Rule => Invalid("A rule is plain words (AIA-016): its expression is written in the rules language, in Rules."),
            DraftItemKinds.SimulationCase when node is JsonObject => Result.Success(),
            DraftItemKinds.SimulationCase => Invalid("A simulation case is an object: description, inputs and expected."),
            _ when node is JsonObject or JsonValue => Result.Success(),
            _ => Invalid("value is an object with the table's columns."),
        };
    }

    /// <summary>The fields to write for one version field's value.</summary>
    public static (ProductDraftFields Fields, DateOnly? EffectiveFrom) Fields(string path, string json)
    {
        var node = JsonNode.Parse(json);
        return path switch
        {
            DraftFields.Name => (new ProductDraftFields(node!.GetValue<string>().Trim(), null, null, null), null),
            DraftFields.Coverages => (new ProductDraftFields(null, node!.AsArray().Select(c => c!.GetValue<string>()).ToList(), null, null), null),
            DraftFields.CoverStartsOn => (new ProductDraftFields(null, null, node!.GetValue<string>(), null), null),
            DraftFields.ReinstatementArrears => (new ProductDraftFields(null, null, null, node!.GetValue<string>()), null),
            DraftFields.EffectiveFrom => (new ProductDraftFields(null, null, null, null),
                DateOnly.ParseExact(node!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    /// <summary>Whether two JSON values are the same, whatever their spacing or key order.</summary>
    public static bool SameJson(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ErrorKind KindOf(string code) => code switch
    {
        ErrorCodes.NotFound => ErrorKind.NotFound,
        ErrorCodes.Forbidden or ErrorCodes.FunctionDisabled or ErrorCodes.FunctionNotGranted or ErrorCodes.FeatureNotEntitled or ErrorCodes.OutOfScope => ErrorKind.Forbidden,
        ErrorCodes.ValidationFailed => ErrorKind.Validation,
        ErrorCodes.PreconditionFailed => ErrorKind.PreconditionFailed,
        _ => ErrorKind.Conflict,
    };

    private static Result Invalid(string message) => Error.Validation(ErrorCodes.ValidationFailed, message);
}

internal sealed class DraftReviewHandlers(IProductDraftRuns runs, IExecutionContext execution, IClock clock, IAuditTrail audit)
    : IQueryHandler<GetDraftItemForReview, DraftItemForReview>, ICommandHandler<DecideDraftItem, DraftItemView>
{
    public async Task<Result<DraftItemForReview>> Handle(GetDraftItemForReview query, CancellationToken cancellationToken) =>
        await runs.Run(query.RunId, cancellationToken) is { } run && run.RequestedBy == execution.ActorId
        && await runs.Item(run.Id, query.ItemId, cancellationToken) is { } item
            ? new DraftItemForReview(run, item)
            : Error.NotFound(ErrorCodes.NotFound, "The run or its value was not found.");

    public async Task<Result<DraftItemView>> Handle(DecideDraftItem command, CancellationToken cancellationToken)
    {
        // The configurator who ran it reviews it (AIA-019): the run and its documents are theirs, as in 7.4.
        if (await runs.Run(command.RunId, cancellationToken) is not { } run || run.RequestedBy != execution.ActorId
            || await runs.Item(run.Id, command.ItemId, cancellationToken) is not { } item)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The run or its value was not found.");
        }

        if (item.RowVersion != command.ExpectedVersion)
        {
            return Error.PreconditionFailed(ErrorCodes.PreconditionFailed, "The value has changed since it was read. Read it again.");
        }

        var decided = item.Decide(command.Decision, command.EditedValue, command.Note, execution.ActorId, clock.UtcNow);
        if (decided.IsFailure)
        {
            return decided.Error;
        }

        // IAM-006: who decided what. The values are product configuration, not personal data; the field and kind say what was decided.
        audit.Record(new AuditEntry("ASSIST", "DecideDraftItem", nameof(DraftItem), item.Id.ToString(), new Dictionary<string, string?>
        {
            ["run"] = run.Id.ToString(), ["product"] = run.ProductCode, ["version"] = run.DraftVersion?.ToString(CultureInfo.InvariantCulture),
            ["kind"] = item.Kind, ["field"] = item.FieldPath, ["flag"] = item.Flag, ["decision"] = item.Decision,
            ["writtenToDraft"] = command.WrittenToDraft ? "true" : "false",
        }));
        return DraftItemView.From(item);
    }
}
