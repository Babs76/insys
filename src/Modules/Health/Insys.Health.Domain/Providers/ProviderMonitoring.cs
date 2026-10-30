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
using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain.Providers;

/// <summary>The measures a provider is watched on (F028, F029, OQ-141).</summary>
public static class ProviderMeasureCodes
{
    public const string ClaimsSubmitted = "CLAIMS_SUBMITTED";
    public const string RejectionRate = "REJECTION_RATE";
    public const string AveragePaid = "AVERAGE_PAID";
    public const string DecisionDays = "DECISION_DAYS";
    public const string RepeatServiceRate = "REPEAT_SERVICE_RATE";
    public const string DisputesPer100 = "DISPUTES_PER_100";
}

public sealed record ProviderMeasureBreached(Guid ResultId, string ProviderId, string MonthKey, string MeasureCode, decimal Value, decimal Threshold, DateTimeOffset OccurredAt)
    : IDomainEvent;

/// <summary>A measure in force: its threshold, the side of it that is a breach, and the fewest claims it is judged on.</summary>
public sealed record ProviderMeasure(string MeasureCode, string? ProviderType, decimal Threshold, string Direction, int MinClaims)
{
    public const string Above = "ABOVE";
    public const string Below = "BELOW";

    public bool Breached(decimal value) => Direction == Above ? value > Threshold : value < Threshold;
}

/// <summary>
/// One provider's measure for one month (health.provider_measure_result). With too few claims it is shown but not judged. A
/// breach asks a person to look: it never changes the provider's status, contract or payment (addendum §4.1).
/// </summary>
public sealed class ProviderMeasureResult : AggregateRoot<Guid>
{
    private ProviderMeasureResult()
    {
    }

    public string ProviderId { get; private set; } = "";

    public string MonthKey { get; private set; } = "";

    public string MeasureCode { get; private set; } = "";

    public decimal Value { get; private set; }

    public int ClaimCount { get; private set; }

    public decimal? Threshold { get; private set; }

    public bool Judged { get; private set; }

    public bool IsBreached { get; private set; }

    public static ProviderMeasureResult Record(Guid id, string providerId, string monthKey, string measureCode, decimal value, int claimCount, ProviderMeasure? measure,
        DateTimeOffset now)
    {
        var judged = measure is not null && claimCount >= measure.MinClaims;
        var result = new ProviderMeasureResult
        {
            Id = Guard.NotEmpty(id), ProviderId = Guard.MaxLength(providerId, 15), MonthKey = monthKey, MeasureCode = Guard.MaxLength(measureCode, 25), Value = value,
            ClaimCount = claimCount, Threshold = measure?.Threshold, Judged = judged, IsBreached = judged && measure!.Breached(value),
        };
        if (result.IsBreached)
        {
            result.Raise(new ProviderMeasureBreached(id, providerId, monthKey, measureCode, value, measure!.Threshold, now));
        }

        result.MarkChanged();
        return result;
    }
}

/// <summary>A person's scored review of a provider's service quality, with a reason (F029).</summary>
public sealed class ProviderQualityReview : AggregateRoot<Guid>
{
    private ProviderQualityReview()
    {
    }

    public string ProviderId { get; private set; } = "";

    public DateOnly ReviewedOn { get; private set; }

    public int Score { get; private set; }

    public string Reason { get; private set; } = "";

    public Guid ReviewedBy { get; private set; }

    public static ProviderQualityReview Record(Guid id, string providerId, DateOnly on, int score, string reason, Guid by)
    {
        var review = new ProviderQualityReview
        {
            Id = Guard.NotEmpty(id), ProviderId = Guard.MaxLength(providerId, 15), ReviewedOn = on, Score = score, Reason = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500),
            ReviewedBy = Guard.NotEmpty(by),
        };
        review.MarkChanged();
        return review;
    }
}

/// <summary>How a statement line compares with INSYS's records (addendum §4.2).</summary>
public static class StatementMatch
{
    public const string Matched = "MATCHED";
    public const string AmountDiffers = "AMOUNT_DIFFERS";
    public const string NotOnOurRecords = "NOT_ON_OUR_RECORDS";
    public const string NotOnStatement = "NOT_ON_STATEMENT";

    /// <summary>The provider lists a claim INSYS paid and then reversed: on our records, but no longer owed to it (Ade, 30 October 2026).</summary>
    public const string ReversedByUs = "REVERSED_BY_US";
}

/// <summary>Where the money of a reversed claim stands, as a statement line shows it (Ade, 30 October 2026).</summary>
public static class ReversalRecovery
{
    public const string Owed = "OWED";
    public const string Offset = "OFFSET";
    public const string Invoiced = "INVOICED";
    public const string Recovered = "RECOVERED";
}

/// <summary>A claim INSYS paid and reversed: what it paid, and the business date of the reversal.</summary>
public sealed record ReversedClaim(decimal Paid, DateOnly ReversedOn);

public static class StatementStatus
{
    public const string Exceptions = "EXCEPTIONS";
    public const string Reconciled = "RECONCILED";
}

public sealed class ProviderStatementLine : Entity<Guid>, ITenantScoped
{
    private ProviderStatementLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid StatementId { get; private set; }

    public string Reference { get; private set; } = "";

    public decimal? StatementAmount { get; private set; }

    public decimal? OurAmount { get; private set; }

    public DateOnly? ReversedOn { get; private set; }

    public string MatchResult { get; private set; } = "";

    public string? Resolution { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public bool Open => MatchResult != StatementMatch.Matched && ResolvedAt is null;

    internal static ProviderStatementLine Create(Guid statementId, string reference, decimal? statementAmount, decimal? ourAmount, string matchResult, DateOnly? reversedOn = null) => new()
    {
        Id = Guid.CreateVersion7(), StatementId = statementId, Reference = Guard.MaxLength(reference, 25), StatementAmount = statementAmount, OurAmount = ourAmount,
        MatchResult = matchResult, ReversedOn = reversedOn,
    };

    internal void Resolve(string reason, Guid by, DateTimeOffset now)
    {
        Resolution = Guard.MaxLength(Guard.NotNullOrWhiteSpace(reason), 500);
        ResolvedBy = by;
        ResolvedAt = now;
    }
}

/// <summary>What the provider's statement says, and what INSYS holds for that reference: its paid amount, or null.</summary>
public sealed record StatementLineInput(string Reference, decimal Amount);

/// <summary>
/// A provider's statement matched against its paid claims (F030): each line, and each paid claim of the period it leaves out.
/// Reconciling changes no payment. It is RECONCILED when no exception is open; a person resolves each with a reason.
/// </summary>
public sealed class ProviderStatement : AggregateRoot<Guid>
{
    private readonly List<ProviderStatementLine> _lines = [];

    private ProviderStatement()
    {
    }

    public string ProviderId { get; private set; } = "";

    public string StatementRef { get; private set; } = "";

    public DateOnly PeriodFrom { get; private set; }

    public DateOnly PeriodTo { get; private set; }

    public string Currency { get; private set; } = "";

    public string Status { get; private set; } = StatementStatus.Exceptions;

    public IReadOnlyList<ProviderStatementLine> Lines => _lines;

    /// <param name="paid">The provider's paid claims of the period, and any others the lines name: claim number and amount paid.</param>
    /// <param name="reversed">The claims the lines name that INSYS paid and reversed. One the provider leaves out needs no line: both sides agree nothing is owed.</param>
    public static Result<ProviderStatement> Match(Guid id, string providerId, string statementRef, DateOnly from, DateOnly to, string currency,
        IReadOnlyList<StatementLineInput> lines, IReadOnlyDictionary<string, decimal> paid, IReadOnlySet<string> inPeriod,
        IReadOnlyDictionary<string, ReversedClaim>? reversed = null)
    {
        if (to < from)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "The statement's period ends before it starts.");
        }

        if (lines.GroupBy(l => l.Reference, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A reference appears on the statement twice.");
        }

        var statement = new ProviderStatement
        {
            Id = Guard.NotEmpty(id), ProviderId = Guard.MaxLength(providerId, 15), StatementRef = Guard.MaxLength(Guard.NotNullOrWhiteSpace(statementRef), 40), PeriodFrom = from,
            PeriodTo = to, Currency = Guard.MaxLength(currency, 3),
        };
        foreach (var line in lines)
        {
            if (reversed is not null && reversed.TryGetValue(line.Reference, out var claim))
            {
                statement._lines.Add(ProviderStatementLine.Create(id, line.Reference, line.Amount, claim.Paid, StatementMatch.ReversedByUs, claim.ReversedOn));
                continue;
            }

            var ours = paid.TryGetValue(line.Reference, out var amount) ? amount : (decimal?)null;
            var result = ours is null ? StatementMatch.NotOnOurRecords : ours == line.Amount ? StatementMatch.Matched : StatementMatch.AmountDiffers;
            statement._lines.Add(ProviderStatementLine.Create(id, line.Reference, line.Amount, ours, result));
        }

        foreach (var reference in inPeriod.Except(lines.Select(l => l.Reference), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            statement._lines.Add(ProviderStatementLine.Create(id, reference, null, paid[reference], StatementMatch.NotOnStatement));
        }

        statement.Settle();
        statement.MarkChanged();
        return statement;
    }

    public Result ResolveLine(Guid lineId, string reason, Guid by, DateTimeOffset now)
    {
        if (_lines.SingleOrDefault(l => l.Id == lineId) is not { } line)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The statement has no such line.");
        }

        if (!line.Open)
        {
            return Error.Conflict(ErrorCodes.InvalidStateTransition, "Only an open exception is resolved.");
        }

        line.Resolve(reason, by, now);
        Settle();
        MarkChanged();
        return Result.Success();
    }

    private void Settle() => Status = _lines.Any(l => l.Open) ? StatementStatus.Exceptions : StatementStatus.Reconciled;
}
