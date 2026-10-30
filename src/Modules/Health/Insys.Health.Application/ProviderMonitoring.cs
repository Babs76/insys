/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Health
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Health.Domain;
using Insys.Health.Domain.Care;
using Insys.Health.Domain.Providers;

namespace Insys.Health.Application;

/// <summary>What one provider's claims come to in a month: the facts its measures are worked from. No clinical data.</summary>
public sealed record ProviderClaimFact(string ProviderId, MedicalClaimStatus Status, decimal? TotalPaid, DateTimeOffset SubmittedAt, DateTimeOffset? AdjudicatedAt, int Lines, int RepeatLines);

/// <summary>The providers whose claims of the month have no results yet (the monthly job's work).</summary>
public sealed record FindProvidersToMeasure(string MonthKey) : IQuery<IReadOnlyList<string>>;

/// <summary>Works out one provider's measures for a month (addendum §4.1). The job runs it once per provider and month.</summary>
public sealed record MeasureProvider(string ProviderId, string MonthKey) : ICommand<int>;

public sealed record ProviderMeasureView(Guid Id, string ProviderId, string MonthKey,
    [property: OneOf(ProviderMeasureCodes.ClaimsSubmitted, ProviderMeasureCodes.RejectionRate, ProviderMeasureCodes.AveragePaid, ProviderMeasureCodes.DecisionDays,
        ProviderMeasureCodes.RepeatServiceRate, ProviderMeasureCodes.DisputesPer100)] string MeasureCode,
    decimal Value, int ClaimCount, decimal? Threshold, bool Judged, bool Breached);

public sealed record ListProviderMeasures(string ProviderId, string? MonthKey, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProviderMeasureView>>;

public sealed record RecordProviderQualityReview(string ProviderId, int Score, string Reason) : ICommand<ProviderQualityReviewView>;

public sealed record ProviderQualityReviewView(Guid Id, string ProviderId, DateOnly ReviewedOn, int Score, string Reason, Guid ReviewedBy);

public sealed record ListProviderQualityReviews(string ProviderId, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<ProviderQualityReviewView>>;

public sealed class RecordProviderQualityReviewValidator : AbstractValidator<RecordProviderQualityReview>
{
    public RecordProviderQualityReviewValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Score).InclusiveBetween(1, 5);
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

/// <summary>A statement entered by a person (F030, OQ-142).</summary>
public sealed record SubmitProviderStatement(string ProviderId, string StatementRef, DateOnly PeriodFrom, DateOnly PeriodTo, string Currency,
    IReadOnlyList<StatementLineInput> Lines) : ICommand<ProviderStatementView>;

/// <summary>The same in the fixed INSYS layout (OQ-142): a header <c>claim_number,amount</c>, then one line per claim.</summary>
public sealed record ImportProviderStatement(string ProviderId, string StatementRef, DateOnly PeriodFrom, DateOnly PeriodTo, string Currency, string Csv)
    : ICommand<ProviderStatementView>;

public sealed record ResolveStatementLine(Guid StatementId, Guid LineId, string Reason) : ICommand<ProviderStatementView>;

public sealed record GetProviderStatement(Guid Id) : IQuery<ProviderStatementView>;

public sealed record ListProviderStatements(string? ProviderId, [property: OneOf(StatementStatus.Exceptions, StatementStatus.Reconciled)] string? Status, Guid? AfterId, int Limit)
    : IQuery<IReadOnlyList<ProviderStatementSummary>>;

public sealed record ProviderStatementSummary(Guid Id, string ProviderId, string StatementRef, DateOnly PeriodFrom, DateOnly PeriodTo,
    [property: OneOf(StatementStatus.Exceptions, StatementStatus.Reconciled)] string Status);

public sealed record ProviderStatementLineView(Guid Id, string Reference, decimal? StatementAmount, decimal? OurAmount,
    [property: OneOf(StatementMatch.Matched, StatementMatch.AmountDiffers, StatementMatch.NotOnOurRecords, StatementMatch.NotOnStatement)] string MatchResult,
    string? Resolution, Guid? ResolvedBy, DateTimeOffset? ResolvedAt);

public sealed record ProviderStatementView(Guid Id, string ProviderId, string StatementRef, DateOnly PeriodFrom, DateOnly PeriodTo, string Currency,
    [property: OneOf(StatementStatus.Exceptions, StatementStatus.Reconciled)] string Status, IReadOnlyList<ProviderStatementLineView> Lines, long RowVersion)
{
    public static ProviderStatementView From(ProviderStatement s) => new(s.Id, s.ProviderId, s.StatementRef, s.PeriodFrom, s.PeriodTo, s.Currency, s.Status,
        s.Lines.OrderBy(l => l.Reference, StringComparer.Ordinal).Select(l => new ProviderStatementLineView(l.Id, l.Reference, l.StatementAmount, l.OurAmount, l.MatchResult,
            l.Resolution, l.ResolvedBy, l.ResolvedAt)).ToList(), s.RowVersion);
}

public sealed class SubmitProviderStatementValidator : AbstractValidator<SubmitProviderStatement>
{
    public SubmitProviderStatementValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.StatementRef).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Currency).Length(3);
        RuleForEach(c => c.Lines).ChildRules(l => l.RuleFor(x => x.Reference).NotEmpty().MaximumLength(25));
    }
}

public sealed class ImportProviderStatementValidator : AbstractValidator<ImportProviderStatement>
{
    public ImportProviderStatementValidator()
    {
        RuleFor(c => c.ProviderId).NotEmpty().MaximumLength(15);
        RuleFor(c => c.StatementRef).NotEmpty().MaximumLength(40);
        RuleFor(c => c.Currency).Length(3);
        RuleFor(c => c.Csv).NotEmpty();
    }
}

public sealed class ResolveStatementLineValidator : AbstractValidator<ResolveStatementLine>
{
    public ResolveStatementLineValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
}

/// <summary>The fixed statement layout (OQ-142).</summary>
public static class ProviderStatementCsv
{
    public const string Header = "claim_number,amount";

    public static Result<IReadOnlyList<StatementLineInput>> Parse(string csv)
    {
        var rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rows.Length == 0 || !string.Equals(rows[0], Header, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, $"The statement's first line must be {Header}.");
        }

        var lines = new List<StatementLineInput>();
        for (var i = 1; i < rows.Length; i++)
        {
            var cells = rows[i].Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length != 2 || cells[0].Length is 0 or > 25
                || !decimal.TryParse(cells[1], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
            {
                return Error.Validation(ErrorCodes.ValidationFailed, $"Line {i + 1} is not a claim number and an amount.");
            }

            lines.Add(new StatementLineInput(cells[0], amount));
        }

        return lines;
    }
}

internal sealed class ProviderMonitoringHandlers(IHealthRepository health, IHealthCodes codes, Contracts.IProviderDirectory providers,
    Workflow.Contracts.IProviderCaseCounts cases, ITenantContext tenant, IExecutionContext execution, IBusinessDate businessDate, IClock clock, IAuditTrail audit) :
    IQueryHandler<FindProvidersToMeasure, IReadOnlyList<string>>,
    ICommandHandler<MeasureProvider, int>,
    IQueryHandler<ListProviderMeasures, IReadOnlyList<ProviderMeasureView>>,
    ICommandHandler<RecordProviderQualityReview, ProviderQualityReviewView>,
    IQueryHandler<ListProviderQualityReviews, IReadOnlyList<ProviderQualityReviewView>>,
    ICommandHandler<SubmitProviderStatement, ProviderStatementView>,
    ICommandHandler<ImportProviderStatement, ProviderStatementView>,
    ICommandHandler<ResolveStatementLine, ProviderStatementView>,
    IQueryHandler<GetProviderStatement, ProviderStatementView>,
    IQueryHandler<ListProviderStatements, IReadOnlyList<ProviderStatementSummary>>
{
    private (DateTimeOffset From, DateTimeOffset To) Month(string monthKey)
    {
        var first = DateOnly.ParseExact(monthKey + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return (businessDate.StartOf(first), businessDate.StartOf(first.AddMonths(1)));
    }

    public async Task<Result<IReadOnlyList<string>>> Handle(FindProvidersToMeasure query, CancellationToken cancellationToken)
    {
        var (from, to) = Month(query.MonthKey);
        return Result.Success(await health.ProvidersToMeasure(query.MonthKey, from, to, cancellationToken));
    }

    public async Task<Result<int>> Handle(MeasureProvider command, CancellationToken cancellationToken)
    {
        var (from, to) = Month(command.MonthKey);
        var day = businessDate.Today;
        var facts = await health.ProviderClaimFacts(command.ProviderId, from, to, cancellationToken);
        var type = await codes.ProviderType(command.ProviderId, day, cancellationToken);
        var measures = (await codes.ProviderMeasures(day, cancellationToken)).Where(m => m.ProviderType is null || m.ProviderType == type)
            .GroupBy(m => m.MeasureCode).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.ProviderType is not null).First());
        var disputes = await cases.Count(tenant.RequireTenantId(), command.ProviderId, from, to, cancellationToken);
        var count = facts.Count;
        var decided = facts.Where(f => f.AdjudicatedAt is not null).ToList();
        var paid = facts.Where(f => f.Status is MedicalClaimStatus.Paid && f.TotalPaid is not null).ToList();
        var lines = facts.Sum(f => f.Lines);
        var values = new Dictionary<string, decimal>
        {
            [ProviderMeasureCodes.ClaimsSubmitted] = count,
            [ProviderMeasureCodes.RejectionRate] = Ratio(facts.Count(f => f.Status == MedicalClaimStatus.Rejected), count),
            [ProviderMeasureCodes.AveragePaid] = paid.Count == 0 ? 0 : Math.Round(paid.Sum(f => f.TotalPaid!.Value) / paid.Count, 2, MidpointRounding.AwayFromZero),
            [ProviderMeasureCodes.DecisionDays] = decided.Count == 0 ? 0 : Math.Round((decimal)decided.Average(f => (f.AdjudicatedAt!.Value - f.SubmittedAt).TotalDays), 4),
            [ProviderMeasureCodes.RepeatServiceRate] = Ratio(facts.Sum(f => f.RepeatLines), lines),
            [ProviderMeasureCodes.DisputesPer100] = count == 0 ? 0 : Math.Round(disputes * 100m / count, 4),
        };
        foreach (var (code, value) in values)
        {
            health.Add(ProviderMeasureResult.Record(Guid.CreateVersion7(), command.ProviderId, command.MonthKey, code, value, count, measures.GetValueOrDefault(code), clock.UtcNow));
        }

        return values.Count;
    }

    private static decimal Ratio(int part, int whole) => whole == 0 ? 0 : Math.Round((decimal)part / whole, 4);

    public async Task<Result<IReadOnlyList<ProviderMeasureView>>> Handle(ListProviderMeasures query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ProviderMeasureView>>((await health.ProviderMeasures(query.ProviderId, query.MonthKey, query.AfterId, query.Limit, cancellationToken))
            .Select(r => new ProviderMeasureView(r.Id, r.ProviderId, r.MonthKey, r.MeasureCode, r.Value, r.ClaimCount, r.Threshold, r.Judged, r.IsBreached)).ToList());

    public async Task<Result<ProviderQualityReviewView>> Handle(RecordProviderQualityReview command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (!await providers.IsActive(tenant.RequireTenantId(), command.ProviderId, businessDate.Today, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The provider was not found.");
        }

        var review = ProviderQualityReview.Record(Guid.CreateVersion7(), command.ProviderId, businessDate.Today, command.Score, command.Reason, user.Value);
        health.Add(review);
        audit.Record(new AuditEntry("HEALTH", "RecordProviderQualityReview", nameof(ProviderQualityReview), review.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = command.ProviderId, ["score"] = command.Score.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        return Review(review);
    }

    public async Task<Result<IReadOnlyList<ProviderQualityReviewView>>> Handle(ListProviderQualityReviews query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<ProviderQualityReviewView>>((await health.ProviderQualityReviews(query.ProviderId, query.AfterId, query.Limit, cancellationToken)).Select(Review).ToList());

    private static ProviderQualityReviewView Review(ProviderQualityReview r) => new(r.Id, r.ProviderId, r.ReviewedOn, r.Score, r.Reason, r.ReviewedBy);

    public Task<Result<ProviderStatementView>> Handle(SubmitProviderStatement command, CancellationToken cancellationToken) =>
        Match(command.ProviderId, command.StatementRef, command.PeriodFrom, command.PeriodTo, command.Currency, command.Lines, cancellationToken);

    public async Task<Result<ProviderStatementView>> Handle(ImportProviderStatement command, CancellationToken cancellationToken)
    {
        var parsed = ProviderStatementCsv.Parse(command.Csv);
        return parsed.IsFailure
            ? parsed.Error
            : await Match(command.ProviderId, command.StatementRef, command.PeriodFrom, command.PeriodTo, command.Currency, parsed.Value, cancellationToken);
    }

    private async Task<Result<ProviderStatementView>> Match(string providerId, string statementRef, DateOnly from, DateOnly to, string currency,
        IReadOnlyList<StatementLineInput> lines, CancellationToken cancellationToken)
    {
        if (!await providers.IsActive(tenant.RequireTenantId(), providerId, businessDate.Today, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.NotFound, "The provider was not found.");
        }

        if (await health.ProviderStatementExists(providerId, statementRef, cancellationToken))
        {
            return Error.Conflict(HealthErrorCodes.ProviderStatementDuplicate, $"Statement {statementRef} of provider {providerId} is already recorded.");
        }

        var paid = await health.ProviderPaidClaims(providerId, from, to, lines.Select(l => l.Reference).ToList(), cancellationToken);
        var statement = ProviderStatement.Match(Guid.CreateVersion7(), providerId, statementRef, from, to, currency, lines,
            paid.ToDictionary(c => c.ClaimNumber, c => c.Paid, StringComparer.Ordinal),
            paid.Where(c => c.InPeriod).Select(c => c.ClaimNumber).ToHashSet(StringComparer.Ordinal));
        if (statement.IsFailure)
        {
            return statement.Error;
        }

        health.Add(statement.Value);
        audit.Record(new AuditEntry("HEALTH", "RecordProviderStatement", nameof(ProviderStatement), statement.Value.Id.ToString(),
            new Dictionary<string, string?> { ["provider"] = providerId, ["statement"] = statementRef }));
        return ProviderStatementView.From(statement.Value);
    }

    public async Task<Result<ProviderStatementView>> Handle(ResolveStatementLine command, CancellationToken cancellationToken)
    {
        var user = Users.Require(execution);
        if (user.IsFailure)
        {
            return user.Error;
        }

        if (await health.ProviderStatement(command.StatementId, cancellationToken) is not { } statement)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The statement was not found.");
        }

        var resolved = statement.ResolveLine(command.LineId, command.Reason, user.Value, clock.UtcNow);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        audit.Record(new AuditEntry("HEALTH", "ResolveStatementLine", nameof(ProviderStatement), statement.Id.ToString(),
            new Dictionary<string, string?> { ["line"] = command.LineId.ToString(), ["reason"] = command.Reason }));
        return ProviderStatementView.From(statement);
    }

    public async Task<Result<ProviderStatementView>> Handle(GetProviderStatement query, CancellationToken cancellationToken) =>
        await health.ProviderStatement(query.Id, cancellationToken) is { } statement
            ? ProviderStatementView.From(statement)
            : Error.NotFound(ErrorCodes.NotFound, "The statement was not found.");

    public async Task<Result<IReadOnlyList<ProviderStatementSummary>>> Handle(ListProviderStatements query, CancellationToken cancellationToken) =>
        Result.Success(await health.ProviderStatements(query.ProviderId, query.Status, query.AfterId, query.Limit, cancellationToken));
}
