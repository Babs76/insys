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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insys.Assist.Domain;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Rating.Contracts;
using Microsoft.Extensions.Options;

namespace Insys.Assist.Application;

/// <summary>
/// The prompt templates (AIA-074): INSYS's own words, with a version each, never interpolated with data (AIA-061). Batch 7.3 moves them into configuration and
/// tunes them on the AI-2 evaluation set; these are the first versions, used with the test stub.
/// </summary>
public static class PromptTemplates
{
    public const string RefusalVersion = "refusal-v1";
    public const string PremiumVersion = "premium-v1";

    public const string Refusal = """
        You explain to an insurance company's staff member why INSYS refused what they asked, using only the facts between the data tags.
        Write plain English, at most 120 words. Say what the refusal means for them, then what to do next.
        Use no number, amount, date or name that isn't in the facts, and keep every amount's currency as given.
        Never say that you can change, approve or retry anything: you can only explain.
        """;

    public const string Premium = """
        You explain to an insurance company's staff member how INSYS calculated a recorded premium, using only the facts between the data tags.
        Write plain English, at most 150 words. Walk through the lines of the calculation in order, then state the premium.
        Use no number, amount, date or name that isn't in the facts, and keep every amount's currency as given. Don't add or round anything.
        Never say that you can change, approve or recalculate anything: you can only explain.
        """;
}

public sealed record ExplanationFactView(string Name, string Value);

/// <summary>
/// The recorded data an explanation is based on (AIA-034): its kind, its reference and, for a calculation, the API route that reads it; for a refusal, the record it
/// was about as the screen named it, kept for the link and the audit and never sent to a model (AIA-039).
/// </summary>
public sealed record ExplanationBasis(string Kind, string Reference, string? Href, Guid? CorrelationId, string? RecordType = null, Guid? RecordId = null);

/// <param name="Outcome">EXPLAINED, or REJECTED, UNAVAILABLE or NO_WORDING, when the plain <paramref name="Facts"/> are shown instead (AIA-033, P8).</param>
/// <param name="Label">The line the console shows with an explanation (AIA-034).</param>
/// <param name="Reused">A stored explanation of the same facts, shown with no new call (AIA-036).</param>
public sealed record ExplanationView(Guid Id, string Kind, string Outcome, string? Text, string Label, ExplanationBasis BasedOn, IReadOnlyList<ExplanationFactView> Facts,
    bool Reused, IReadOnlyList<string> RejectedNumbers);

/// <summary>The refusal the screen received (AIA-039): its code, the names of the fields in error, the record it was about, and its correlation ID.</summary>
public sealed record ExplainRefusalRequest(string Code, IReadOnlyList<string>? Fields, string? RecordType, Guid? RecordId, Guid? CorrelationId);

public sealed record ExplainPremiumRequest(Guid CalculationId, Guid? CorrelationId);

/// <summary>The newest explained explanation with this cache key, if any (AIA-036).</summary>
public sealed record FindExplained(string CacheKey) : IQuery<AssistExplanation?>;

internal sealed class FindExplainedHandler(IAssistRepository assist) : IQueryHandler<FindExplained, AssistExplanation?>
{
    public async Task<Result<AssistExplanation?>> Handle(FindExplained query, CancellationToken cancellationToken) =>
        Result<AssistExplanation?>.Success(await assist.Explained(query.CacheKey, cancellationToken));
}

/// <summary>A user's mark on an explanation (AIA-037).</summary>
public sealed record GiveExplanationFeedback(Guid ExplanationId, bool Helpful, string? Reason, string? Comment) : ICommand<Guid>;

internal sealed class GiveExplanationFeedbackHandler(IAssistRepository assist, IExecutionContext execution, IAuditTrail audit) : ICommandHandler<GiveExplanationFeedback, Guid>
{
    public async Task<Result<Guid>> Handle(GiveExplanationFeedback command, CancellationToken cancellationToken)
    {
        // Only the user it was shown to marks it (AIA-037): anyone else is told it doesn't exist.
        if (!await assist.ExplanationShownTo(command.ExplanationId, execution.ActorId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The explanation was not found.");
        }

        var feedback = AssistExplanationFeedback.Give(command.ExplanationId, command.Helpful, command.Reason, command.Comment);
        if (feedback.IsFailure)
        {
            return feedback.Error;
        }

        assist.Add(feedback.Value);
        audit.Record(new AuditEntry("ASSIST", "GiveExplanationFeedback", nameof(AssistExplanationFeedback), feedback.Value.Id.ToString(), new Dictionary<string, string?>
        {
            ["explanation"] = command.ExplanationId.ToString(), ["helpful"] = command.Helpful.ToString(), ["reason"] = feedback.Value.Reason,
        }));
        return feedback.Value.Id;
    }
}

/// <summary>
/// AI-2's explanations (SRS §5, addendum §3). The facts come only from INSYS's own records, read as the user may read them, never from what the screen sends
/// beyond the refusal's code, field names and references (AIA-039). Nothing is held open while the model answers (AIA-072): the settings, the cache and the cap
/// are read in short reads, and the record is written in a short command after. When no explanation can be shown, the plain facts are (AIA-033, P8).
/// </summary>
public sealed partial class Explainer(AssistRunner runner, IDispatcher dispatcher, IErrorCatalogue catalogue, IRecordedCalculations calculations, IExecutionContext execution,
    IOptions<AssistOptions> options)
{
    public const string Label = "AI-generated explanation of a recorded INSYS decision";

    /// <summary>At most this many field names of a refusal are sent.</summary>
    public const int MaxFields = 30;

    public async Task<Result<ExplanationView>> Refusal(ExplainRefusalRequest request, CancellationToken cancellationToken)
    {
        if (catalogue.Find(request.Code) is not { } entry)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "code is not a code in the error catalogue.");
        }

        if (request.Fields is { Count: > MaxFields })
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"At most {MaxFields} field names are explained.");
        }

        if (request.RecordType is { } type && !RecordTypeForm().IsMatch(type))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "recordType is a record type's code, such as BILLING_ACCOUNT.");
        }

        // Only what INSYS itself holds about the code (D-15). The fields' names are the client's, so only how many there are is sent; the catalogue's status isn't
        // sent either, as a code can be raised with another (the screen has the one it got).
        var facts = new AllowedFacts().Code("code", entry.Code).Flag("worth retrying", entry.Retryable);
        if (request.Fields is { Count: > 0 } fields)
        {
            facts.Number("fields in error", fields.Count);
        }

        var basis = new ExplanationBasis("REFUSAL", entry.Code, null, request.CorrelationId, request.RecordType, request.RecordId);
        if (entry.Means is null)
        {
            // No catalogue wording yet (AIA-031): nothing to ground an explanation in, so no call is made and the plain refusal is shown.
            var allowed = await dispatcher.Query(new CheckAssistRun(AssistFeatures.ExplainRefusal), cancellationToken);
            return allowed.IsFailure ? allowed.Error : await Record(ExplanationKinds.Refusal, entry.Code, CacheKey(AssistFeatures.ExplainRefusal, PromptTemplates.RefusalVersion, facts),
                PromptTemplates.RefusalVersion, ExplanationOutcomes.NoWording, facts, null, null, null, basis, [], cancellationToken);
        }

        facts.Wording("what it means", entry.Means);
        if (entry.Remedy is { Length: > 0 } remedy)
        {
            facts.Wording("what to do next", remedy);
        }

        return await Explain(AssistFeatures.ExplainRefusal, ExplanationKinds.Refusal, entry.Code, PromptTemplates.RefusalVersion, PromptTemplates.Refusal, facts, basis, request.CorrelationId,
            cancellationToken);
    }

    public async Task<Result<ExplanationView>> Premium(ExplainPremiumRequest request, CancellationToken cancellationToken)
    {
        // The calculation as the user may read it (AIA-003): explaining it needs reading it.
        if (!execution.HasPermission(RatingPermissions.CalculationRead))
        {
            return Error.Forbidden(ErrorCodes.FunctionNotGranted, "Explaining a premium calculation needs reading it: none of your roles grants reading premium calculations.");
        }

        if (await calculations.Get(request.CalculationId, cancellationToken) is not { } calculation)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The calculation was not found.");
        }

        // Only the recorded calculation (AIA-032), and of it only what Rating itself recorded from the product's configuration: its product and its lines, each with
        // the step, the configured factor, the table value used and the amount with its currency. The inputs are left out: their names and values are the caller's
        // (D-15), and the lines show what each one did. For Health, the factor values are left out too, so no clinical detail can appear (AIA-038).
        var health = calculation.SourceDomain == "HEALTH";
        var facts = new AllowedFacts().Code("product", calculation.ProductCode).Number("product version", calculation.ProductVersion)
            .Number("rating version", calculation.RatingVersion).Code("source", calculation.SourceDomain);
        foreach (var line in calculation.Lines.OrderBy(l => l.LineNo))
        {
            facts.Code($"line {line.LineNo}", line.Component);
            if (line.FactorCode is not null)
            {
                facts.Code($"line {line.LineNo} factor", line.FactorCode);
            }

            if (line.FactorValue is { } value && !health)
            {
                if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    facts.Number($"line {line.LineNo} factor value", number);
                }
                else
                {
                    facts.Code($"line {line.LineNo} factor value", value);
                }
            }

            facts.Amount($"line {line.LineNo} amount", line.Amount, calculation.Currency);
        }

        facts.Amount("premium", calculation.Premium, calculation.Currency);
        var basis = new ExplanationBasis("PREMIUM_CALCULATION", calculation.Id.ToString(), $"/api/v1/rating/calculations/{calculation.Id}", request.CorrelationId);
        return await Explain(AssistFeatures.ExplainPremium, ExplanationKinds.Premium, calculation.Id.ToString(), PromptTemplates.PremiumVersion, PromptTemplates.Premium, facts, basis,
            request.CorrelationId, cancellationToken);
    }

    private async Task<Result<ExplanationView>> Explain(string feature, string kind, string sourceRef, string promptVersion, string instructions, AllowedFacts facts, ExplanationBasis basis,
        Guid? correlationId, CancellationToken cancellationToken)
    {
        var allowed = await dispatcher.Query(new CheckAssistRun(feature), cancellationToken);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var key = CacheKey(feature, promptVersion, facts);
        var reused = await dispatcher.Query(new FindExplained(key), cancellationToken);
        if (reused is { IsSuccess: true, Value: { } stored })
        {
            return await Record(kind, sourceRef, key, promptVersion, ExplanationOutcomes.Explained, facts, stored.Text, null, stored.Id, basis, [], cancellationToken);
        }

        var call = await runner.Call(new AssistRunRequest(feature, promptVersion, instructions, facts, correlationId), cancellationToken);
        if (call.IsFailure)
        {
            return call.Error;
        }

        var outcome = call.Value.Outcome.Status switch
        {
            AssistRunStatuses.Verified => ExplanationOutcomes.Explained,
            AssistRunStatuses.Rejected => ExplanationOutcomes.Rejected,
            _ => ExplanationOutcomes.Unavailable,
        };
        return await Record(kind, sourceRef, key, promptVersion, outcome, facts, call.Value.Outcome.Text, call.Value, null, basis, call.Value.Outcome.RejectedNumbers, cancellationToken);
    }

    private async Task<Result<ExplanationView>> Record(string kind, string sourceRef, string key, string promptVersion, string outcome, AllowedFacts facts, string? text, AssistCall? call,
        Guid? reusedFrom, ExplanationBasis basis, IReadOnlyList<string> rejected, CancellationToken cancellationToken)
    {
        var factViews = facts.Items.Select(f => new ExplanationFactView(f.Name, f.Value)).ToList();
        var explanation = AssistExplanation.Record(kind, sourceRef, key, promptVersion, options.Value.ModelVersion, outcome, JsonSerializer.Serialize(factViews), text, call?.Run.Id,
            reusedFrom, basis.CorrelationId);
        var audit = basis.RecordType is null && basis.RecordId is null
            ? null
            : new Dictionary<string, string?> { ["recordType"] = basis.RecordType, ["recordId"] = basis.RecordId?.ToString() };
        var recorded = await dispatcher.Send(new RecordAssistRun(call?.Run, call?.Payload, explanation, audit), cancellationToken);
        return recorded.IsFailure
            ? recorded.Error
            : new ExplanationView(explanation.Id, kind, outcome, explanation.Text, Label, basis, factViews, reusedFrom is not null, rejected);
    }

    /// <summary>The cache key (AIA-036): a hash of the feature, the prompt and model versions, and exactly the facts sent, so the same facts reuse an explanation.</summary>
    private string CacheKey(string feature, string promptVersion, AllowedFacts facts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{feature}\n{promptVersion}\n{options.Value.ModelVersion}\n{facts.Render()}"))).ToLowerInvariant();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,39}$")]
    private static partial Regex RecordTypeForm();
}
