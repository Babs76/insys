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
using System.Text.RegularExpressions;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.ProductFactory.Contracts;

namespace Insys.Assist.Application;

public static class ProvenanceOutcomes
{
    public const string Undecided = "UNDECIDED";

    /// <summary>Dropped by INSYS's checks (AIA-013, AIA-015): never decided, never in the Draft.</summary>
    public const string Dropped = "DROPPED";
}

/// <param name="Outcome">ACCEPTED, EDITED, REJECTED, UNDECIDED or DROPPED.</param>
/// <param name="DraftValue">For a version's own field, the Draft's value now; <paramref name="MatchesDraft"/> says whether it is what was decided.</param>
public sealed record ProvenanceItem(Guid Id, string Kind, string Field, string Flag, string? Value, Guid? CitationDocumentId, string? CitationFile, int? CitationPage,
    string? CitationQuote, string CheckResult, string? CheckReason, string Outcome, string? EditedValue, string? DecisionNote, string? DecidedBy, DateTimeOffset? DecidedAt,
    string? DraftValue, bool? MatchesDraft);

/// <param name="InterpretedUndecided">Interpreted values not yet reviewed: §8.1 step 5 says they can't be left so.</param>
/// <param name="DraftDiffers">Version fields whose Draft value isn't what was decided: changed outside the review, or a decision recorded after its write failed.</param>
/// <param name="ReviewComplete">No Interpreted value undecided and no version field differing from its decision.</param>
public sealed record ProvenanceSummary(int Items, int Accepted, int Edited, int Rejected, int Undecided, int Dropped, int InterpretedUndecided, int MissingUnfilled,
    int DraftDiffers, bool ReviewComplete);

/// <summary>
/// AIA-020: the provenance report of a version AI-1 drafted: every proposed value with its flag, citation, INSYS's check, and whether it was accepted, edited
/// or rejected, by whom and when, with each version field compared with the Draft as it is now.
/// </summary>
public sealed record ProductDraftProvenanceView(string ProductCode, int Version, string VersionStatus, Guid RunId, string RequestedBy, DateTimeOffset RequestedAt,
    IReadOnlyList<ProductDraftDocumentView> Documents, IReadOnlyList<ProvenanceItem> Items, ProvenanceSummary Summary);

/// <summary>The run that drafted the version, its documents and items, for the provenance report: whoever may read the report, not only its requester.</summary>
public sealed record GetDraftRunForVersion(string ProductCode, int Version) : IQuery<ProductDraftRunView>;

internal sealed class GetDraftRunForVersionHandler(IProductDraftRuns runs) : IQueryHandler<GetDraftRunForVersion, ProductDraftRunView>
{
    public async Task<Result<ProductDraftRunView>> Handle(GetDraftRunForVersion query, CancellationToken cancellationToken) =>
        await runs.ForVersion(query.ProductCode, query.Version, cancellationToken) is { } run
            ? ProductDraftRunView.From(run, await runs.Documents(run.Id, cancellationToken), await runs.Items(run.Id, cancellationToken))
            : Error.NotFound(ErrorCodes.NotFound, $"Version {query.Version} of {query.ProductCode} wasn't drafted by AI-1.");
}

/// <summary>
/// AIA-020's report for the approver, read as them: the version through Product Factory's read (their scope; a version outside it is not found), then Assist's
/// record of the run. OQ-190 (b), Ade 19 November 2026: Product Factory's submission refuses the version while this review is incomplete
/// (<c>ProductDraftSubmissionCheck</c>). The report isn't attached to the approval in Workflow (option (c), not chosen); the approver's screen reads it by version.
/// </summary>
public sealed class ProductDraftProvenance(IDispatcher dispatcher, IProductDrafts products)
{
    public async Task<Result<ProductDraftProvenanceView>> For(string productCode, int version, CancellationToken cancellationToken)
    {
        if (await products.Version(productCode, version, cancellationToken) is not { } draft)
        {
            return Error.NotFound(ErrorCodes.NotFound, $"Version {version} of {productCode} was not found.");
        }

        var run = await dispatcher.Query(new GetDraftRunForVersion(productCode, version), cancellationToken);
        if (run.IsFailure)
        {
            return run.Error;
        }

        var copiedFrom = version > 1 ? await products.Version(productCode, version - 1, cancellationToken) : null;
        var files = run.Value.Documents.ToDictionary(d => d.DocumentId, d => d.FileName);
        var (items, summary) = Review(run.Value.Items, draft, copiedFrom, files);
        return new ProductDraftProvenanceView(productCode, version, draft.Status, run.Value.Id, run.Value.RequestedBy, run.Value.RequestedAt, run.Value.Documents, items,
            summary);
    }

    /// <summary>
    /// The review's state: each item's outcome, and whether the review is complete. Ade, 19 November 2026: every Interpreted value must be reviewed, a rule or a
    /// simulation case as much as a version field (§8.1 step 5); a rejected field holds the copied version's value. Product Factory's submission check uses it
    /// too (OQ-190 (b)).
    /// </summary>
    public static (IReadOnlyList<ProvenanceItem> Items, ProvenanceSummary Summary) Review(IReadOnlyList<DraftItemView> runItems, ProductDraftVersion draft,
        ProductDraftVersion? copiedFrom, IReadOnlyDictionary<Guid, string?> files)
    {
        var items = runItems.Select(i => Item(i, draft, copiedFrom, files)).ToList();
        var summary = new ProvenanceSummary(items.Count,
            items.Count(i => i.Outcome == DraftItemDecisions.Accepted), items.Count(i => i.Outcome == DraftItemDecisions.Edited),
            items.Count(i => i.Outcome == DraftItemDecisions.Rejected), items.Count(i => i.Outcome == ProvenanceOutcomes.Undecided),
            items.Count(i => i.Outcome == ProvenanceOutcomes.Dropped),
            items.Count(i => i.Outcome == ProvenanceOutcomes.Undecided && i.Flag == DraftItemFlags.Interpreted),
            items.Count(i => i.Outcome == ProvenanceOutcomes.Undecided && i.Flag == DraftItemFlags.Missing),
            items.Count(i => i.MatchesDraft == false), false);
        return (items, summary with { ReviewComplete = summary.InterpretedUndecided == 0 && summary.DraftDiffers == 0 });
    }

    private static ProvenanceItem Item(DraftItemView i, ProductDraftVersion draft, ProductDraftVersion? copiedFrom, IReadOnlyDictionary<Guid, string?> files)
    {
        var outcome = i.CheckResult != "PASSED" ? ProvenanceOutcomes.Dropped : i.Decision ?? ProvenanceOutcomes.Undecided;
        string? draftValue = null;
        bool? matches = null;
        if (i.Kind == DraftItemKinds.Value && DraftReviewRules.FieldOf(draft, i.Field) is { } current)
        {
            draftValue = current;
            var expected = outcome switch
            {
                DraftItemDecisions.Accepted => i.Value,
                DraftItemDecisions.Edited => i.EditedValue,
                DraftItemDecisions.Rejected => copiedFrom is null ? null : DraftReviewRules.FieldOf(copiedFrom, i.Field),
                ProvenanceOutcomes.Undecided when i.InDraft => i.Value,
                _ => null,
            };
            matches = expected is null ? null : DraftReviewRules.SameJson(current, expected);
        }

        return new ProvenanceItem(i.Id, i.Kind, i.Field, i.Flag, i.Value, i.CitationDocumentId,
            i.CitationDocumentId is { } d ? files.GetValueOrDefault(d) : null, i.CitationPage, i.CitationQuote, i.CheckResult, i.CheckReason, outcome, i.EditedValue,
            i.DecisionNote, i.DecidedBy, i.DecidedAt, draftValue, matches);
    }
}

/// <summary>One accepted or edited simulation case, as a line of Rating's test case library import (rating.cfg_rating_test_case_library).</summary>
public sealed record SimulationCaseLine(string Code, string CaseId, string ProductVersion, JsonElement Inputs, decimal ExpectedPremium, decimal? Tolerance,
    DateOnly EffectiveFrom, string? Description, Guid ItemId);

/// <summary>A reviewed case that can't become a line, and why: no expected premium the documents state, or inputs without the currency and frequency.</summary>
public sealed record SimulationCaseNotExported(Guid ItemId, string? Description, string Reason);

/// <summary>
/// AIA-018 (§8.1 step 6): the configurator's accepted cases, as lines for Rating's test case library, which they import through its own import (BR-016) for a
/// second person's approval; Product Factory's simulation then runs them with the configurator's own. Assist writes nothing to Rating, so no case bypasses
/// validation, simulation or approval (§4.3).
/// </summary>
public sealed record SimulationCaseExportView(string ProductCode, int Version, string Table, IReadOnlyList<SimulationCaseLine> Lines,
    IReadOnlyList<SimulationCaseNotExported> NotExported, int Undecided, int Rejected);

public sealed partial class ProductDraftSimulationCases(IDispatcher dispatcher, IProductDrafts products)
{
    public const string Table = "rating.cfg_rating_test_case_library";
    public const string NoExpectedPremium = "NO_EXPECTED_PREMIUM";
    public const string NoCurrencyOrFrequency = "NO_CURRENCY_OR_FREQUENCY";
    public const string Malformed = "MALFORMED";

    /// <summary>Rating's reference to a product version on its tables ("CODE v3", RatingConfiguration.ProductVersionRef).</summary>
    public static string ProductVersionRef(string productCode, int version) => $"{productCode} v{version.ToString(CultureInfo.InvariantCulture)}";

    public async Task<Result<SimulationCaseExportView>> For(Guid runId, CancellationToken cancellationToken)
    {
        var run = await dispatcher.Query(new GetProductDraft(runId), cancellationToken);
        if (run.IsFailure)
        {
            return run.Error;
        }

        if (run.Value.DraftVersion is not { } version || await products.Version(run.Value.ProductCode, version, cancellationToken) is not { } draft)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "The run hasn't drafted a version, so it has no simulation cases to hand over.");
        }

        var cases = run.Value.Items.Where(i => i.Kind == DraftItemKinds.SimulationCase && i.CheckResult == "PASSED").ToList();
        var lines = new List<SimulationCaseLine>();
        var notExported = new List<SimulationCaseNotExported>();
        var n = 0;
        foreach (var item in cases.Where(i => i.Decision is DraftItemDecisions.Accepted or DraftItemDecisions.Edited))
        {
            var json = item.Decision == DraftItemDecisions.Edited ? item.EditedValue : item.Value;
            var (line, reason) = Line(json, run.Value.ProductCode, version, draft.EffectiveFrom, item.Id, ++n);
            if (line is not null)
            {
                lines.Add(line);
            }
            else
            {
                notExported.Add(new SimulationCaseNotExported(item.Id, Description(json), reason!));
            }
        }

        return new SimulationCaseExportView(run.Value.ProductCode, version, Table, lines, notExported, cases.Count(i => i.Decision is null),
            cases.Count(i => i.Decision == DraftItemDecisions.Rejected));
    }

    /// <summary>A case as a line: its inputs with currency and frequency (Rating's validation), and an expected premium as a number, alone or as expected.premium.</summary>
    public static (SimulationCaseLine? Line, string? Reason) Line(string? json, string productCode, int version, DateOnly effectiveFrom, Guid itemId, int number)
    {
        JsonObject? value;
        try
        {
            value = json is null ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            value = null;
        }

        if (value is null)
        {
            return (null, Malformed);
        }

        var expected = value["expected"];
        decimal? premium = expected is JsonValue plain && plain.TryGetValue<decimal>(out var p) ? p
            : expected is JsonObject o && o["premium"] is JsonValue pv && pv.TryGetValue<decimal>(out var p2) ? p2 : null;
        decimal? tolerance = expected is JsonObject t && t["tolerance"] is JsonValue tv && tv.TryGetValue<decimal>(out var tol) ? tol : null;
        if (premium is null)
        {
            return (null, NoExpectedPremium);
        }

        if (value["inputs"] is not JsonObject inputs || inputs["currency"] is not JsonValue c || !c.TryGetValue<string>(out var currency) || !Currency().IsMatch(currency)
            || inputs["frequency"] is not JsonValue f || !f.TryGetValue<string>(out var frequency) || string.IsNullOrWhiteSpace(frequency))
        {
            return (null, NoCurrencyOrFrequency);
        }

        var caseId = $"AI-{number.ToString(CultureInfo.InvariantCulture)}";
        return (new SimulationCaseLine($"{productCode}-V{version.ToString(CultureInfo.InvariantCulture)}-{caseId}", caseId, ProductVersionRef(productCode, version),
            JsonDocument.Parse(inputs.ToJsonString()).RootElement.Clone(), premium.Value, tolerance, effectiveFrom, Description(json), itemId), null);
    }

    private static string? Description(string? json)
    {
        try
        {
            return json is not null && JsonNode.Parse(json) is JsonObject o && o["description"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();
}
