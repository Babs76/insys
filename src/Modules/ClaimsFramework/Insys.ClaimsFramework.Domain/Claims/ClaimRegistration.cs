/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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
using Insys.BuildingBlocks.Domain.Expressions;

namespace Insys.ClaimsFramework.Domain.Claims;

public sealed record ClaimNumberAllocated(Guid RegistrationId, string ClaimNumber, string Domain, Guid DomainClaimId, Guid PolicyRef, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record FraudIndicatorRaised(string ClaimNumber, string IndicatorCode, int Score, bool Referred, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A fraud indicator (claims.cfg_fraud_indicator_definitions): a condition, its score, and the total score that refers a claim.</summary>
public sealed record FraudIndicator(string IndicatorCode, Expression Logic, int Score, int ReferralThreshold);

/// <summary>Why an indicator was skipped (F009): a fact its condition needs was not given, or the condition could not be evaluated.</summary>
public static class SkipReasons
{
    public const string MissingFacts = "MISSING_FACTS";
    public const string EvaluationFailed = "EVALUATION_FAILED";
}

/// <summary>
/// An indicator that could not be evaluated, and why: the names of the facts it lacked, or the evaluation's error code.
/// Names and codes only, never a fact's value.
/// </summary>
public sealed record SkippedIndicator(string IndicatorCode, string Reason, IReadOnlyList<string> MissingFacts, string? ErrorCode);

/// <summary>An evaluation's total score, and the indicators that could not be evaluated on the claim's facts (F009).</summary>
public sealed record FraudEvaluation(Guid Id, int Score, IReadOnlyList<SkippedIndicator> Skipped)
{
    public IReadOnlyList<string> NotEvaluated => Skipped.Select(s => s.IndicatorCode).ToList();
}

/// <summary>Fraud indicators were skipped on a claim (F009): the tenant's configuration owner is told, not only the log.</summary>
public sealed record FraudIndicatorsSkipped(Guid EvaluationId, string ClaimNumber, IReadOnlyList<SkippedIndicator> Skipped, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Entity FraudEvaluationRecord (claims.fraud_evaluation, V0032): each screening of a claim, with its score and the
/// indicators it skipped and why (F009). Append-only.
/// </summary>
public sealed class FraudEvaluationRecord : Entity<Guid>, ITenantScoped
{
    private FraudEvaluationRecord()
    {
    }

    public Guid TenantId { get; private set; }

    public string ClaimNumber { get; private set; } = "";

    public DateTimeOffset EvaluatedAt { get; private set; }

    public int Score { get; private set; }

    public bool Referred { get; private set; }

    public short IndicatorsEvaluated { get; private set; }

    public IReadOnlyList<SkippedIndicator> Skipped { get; private set; } = [];

    internal static FraudEvaluationRecord Create(Guid id, string claimNumber, int score, bool referred, int evaluated, IReadOnlyList<SkippedIndicator> skipped,
        DateTimeOffset now) =>
        new()
        {
            Id = id, ClaimNumber = claimNumber, Score = score, Referred = referred, IndicatorsEvaluated = checked((short)evaluated), Skipped = skipped,
            EvaluatedAt = now,
        };
}

/// <summary>Entity EvidenceItem (claims.claim_evidence).</summary>
public sealed class EvidenceItem : Entity<Guid>, ITenantScoped
{
    private EvidenceItem()
    {
    }

    public Guid TenantId { get; private set; }

    public string ClaimNumber { get; private set; } = "";

    public string EvidenceType { get; private set; } = "";

    /// <summary>REQUESTED or RECEIVED.</summary>
    public string Status { get; private set; } = "REQUESTED";

    public Guid? DocumentId { get; private set; }

    public DateTimeOffset? RequestedAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public long RowVersion { get; private set; }

    internal static EvidenceItem Request(string claimNumber, string type, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), ClaimNumber = claimNumber, EvidenceType = type, RequestedAt = now };

    internal void Receive(Guid documentId, DateTimeOffset now)
    {
        Status = "RECEIVED";
        DocumentId = documentId;
        ReceivedAt = now;
    }

    internal static EvidenceItem Received(string claimNumber, string type, Guid documentId, DateTimeOffset now)
    {
        var item = Request(claimNumber, type, now);
        item.RequestedAt = null;
        item.Receive(documentId, now);
        return item;
    }
}

/// <summary>Entity Communication (claims.claim_communication): never changed once recorded.</summary>
public sealed class Communication : Entity<Guid>, ITenantScoped
{
    private Communication()
    {
    }

    public Guid TenantId { get; private set; }

    public string ClaimNumber { get; private set; } = "";

    /// <summary>IN or OUT.</summary>
    public string Direction { get; private set; } = "";

    public string Channel { get; private set; } = "";

    public Guid? DocumentId { get; private set; }

    public DateTimeOffset SentAt { get; private set; }

    internal static Communication Create(string claimNumber, string direction, string channel, Guid? documentId, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), ClaimNumber = claimNumber, Direction = direction, Channel = channel, DocumentId = documentId, SentAt = now };
}

/// <summary>Entity FraudIndicatorHit (claims.fraud_indicator_hit).</summary>
public sealed class FraudIndicatorHit : Entity<Guid>, ITenantScoped
{
    private FraudIndicatorHit()
    {
    }

    public Guid TenantId { get; private set; }

    public string ClaimNumber { get; private set; } = "";

    public string IndicatorCode { get; private set; } = "";

    public short Score { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    public bool Referred { get; private set; }

    internal static FraudIndicatorHit Create(string claimNumber, string code, int score, bool referred, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), ClaimNumber = claimNumber, IndicatorCode = code, Score = checked((short)score), Referred = referred, EvaluatedAt = now };
}

/// <summary>
/// The ClaimRegistration aggregate (claims.claim_register): one claim number per domain claim, and a repeat
/// registration returns the same number (CLM-005; also a unique index). The domains own adjudication (CLM-002); this
/// holds what they share (CLM-001): the number, evidence, communications and fraud indicators.
/// </summary>
public sealed class ClaimRegistration : AggregateRoot<Guid>
{
    private readonly List<EvidenceItem> _evidence = [];
    private readonly List<Communication> _communications = [];
    private readonly List<FraudIndicatorHit> _fraudHits = [];
    private readonly List<FraudEvaluationRecord> _fraudEvaluations = [];

    private ClaimRegistration()
    {
    }

    public string ClaimNumber { get; private set; } = "";

    public string Domain { get; private set; } = "";

    public Guid DomainClaimId { get; private set; }

    public Guid PolicyRef { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public IReadOnlyList<EvidenceItem> Evidence => _evidence;

    public IReadOnlyList<Communication> Communications => _communications;

    public IReadOnlyList<FraudIndicatorHit> FraudHits => _fraudHits;

    public IReadOnlyList<FraudEvaluationRecord> FraudEvaluations => _fraudEvaluations;

    /// <summary>
    /// Command AllocateClaimNumber. TODO(spec): numbers are the scheme's prefix and an ID-based suffix; sequence
    /// numbers with check digits need a counter (OQ-058).
    /// </summary>
    public static ClaimRegistration AllocateClaimNumber(Guid id, string prefix, string domain, Guid domainClaimId, Guid policyRef, DateTimeOffset now)
    {
        var registration = new ClaimRegistration
        {
            Id = Guard.NotEmpty(id),
            ClaimNumber = References.Of($"{prefix}-", id, prefix.Length + 13),
            Domain = Guard.MaxLength(Guard.NotNullOrWhiteSpace(domain), 10),
            DomainClaimId = Guard.NotEmpty(domainClaimId),
            PolicyRef = Guard.NotEmpty(policyRef),
            RegisteredAt = now,
        };
        registration.Raise(new ClaimNumberAllocated(id, registration.ClaimNumber, registration.Domain, domainClaimId, policyRef, now));
        registration.MarkChanged();
        return registration;
    }

    public Result RequestEvidence(string evidenceType, DateTimeOffset now)
    {
        if (_evidence.Any(e => e.EvidenceType == evidenceType && e.Status == "REQUESTED"))
        {
            return Error.Conflict(ClaimsErrorCodes.EvidenceRequested, $"{evidenceType} is already requested.");
        }

        _evidence.Add(EvidenceItem.Request(ClaimNumber, evidenceType, now));
        MarkChanged();
        return Result.Success();
    }

    /// <summary>Command RecordEvidence: fulfils an open request of the type, or records evidence that arrived unrequested.</summary>
    public void RecordEvidence(string evidenceType, Guid documentId, DateTimeOffset now)
    {
        if (_evidence.FirstOrDefault(e => e.EvidenceType == evidenceType && e.Status == "REQUESTED") is { } requested)
        {
            requested.Receive(documentId, now);
        }
        else
        {
            _evidence.Add(EvidenceItem.Received(ClaimNumber, evidenceType, documentId, now));
        }

        MarkChanged();
    }

    public void RecordCommunication(string direction, string channel, Guid? documentId, DateTimeOffset now)
    {
        _communications.Add(Communication.Create(ClaimNumber, direction, channel, documentId, now));
        MarkChanged();
    }

    /// <summary>
    /// Command EvaluateFraudIndicators: every indicator whose condition holds is recorded; when their total score reaches
    /// an indicator's referral threshold, the claim is referred. An indicator that cannot be evaluated on these facts (a
    /// fact its domain does not give, say, since the indicators are shared by the domains) is skipped and reported, so
    /// that it never stops the others from screening the claim. The evaluation is recorded with what it skipped and why,
    /// and a skip raises FraudIndicatorsSkipped for the configuration owner (F009).
    /// </summary>
    public FraudEvaluation EvaluateFraudIndicators(IReadOnlyList<FraudIndicator> indicators, IReadOnlyDictionary<string, object?> facts, DateTimeOffset now)
    {
        var hits = new List<FraudIndicator>();
        var skipped = new List<SkippedIndicator>();
        foreach (var indicator in indicators)
        {
            var missing = indicator.Logic.Variables.Where(v => !facts.ContainsKey(v)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                skipped.Add(new SkippedIndicator(indicator.IndicatorCode, SkipReasons.MissingFacts, missing, null));
                continue;
            }

            var holds = indicator.Logic.Evaluate(facts);
            if (holds.IsFailure)
            {
                skipped.Add(new SkippedIndicator(indicator.IndicatorCode, SkipReasons.EvaluationFailed, [], holds.Error.Code));
            }
            else if (holds.Value)
            {
                hits.Add(indicator);
            }
        }

        var total = hits.Sum(h => h.Score);
        foreach (var hit in hits)
        {
            var referred = total >= hit.ReferralThreshold;
            _fraudHits.Add(FraudIndicatorHit.Create(ClaimNumber, hit.IndicatorCode, hit.Score, referred, now));
            Raise(new FraudIndicatorRaised(ClaimNumber, hit.IndicatorCode, hit.Score, referred, now));
        }

        var evaluation = FraudEvaluationRecord.Create(Guid.CreateVersion7(), ClaimNumber, total, hits.Any(h => total >= h.ReferralThreshold),
            indicators.Count - skipped.Count, skipped, now);
        _fraudEvaluations.Add(evaluation);
        if (skipped.Count > 0)
        {
            Raise(new FraudIndicatorsSkipped(evaluation.Id, ClaimNumber, skipped, now));
        }

        MarkChanged();
        return new FraudEvaluation(evaluation.Id, total, skipped);
    }
}

public static class ClaimsErrorCodes
{
    [ErrorCode(409, "error.claims.evidence_requested")]
    public const string EvidenceRequested = "EVIDENCE_ALREADY_REQUESTED";

    [ErrorCode(422, "error.claims.reserve_below_zero")]
    public const string ReserveBelowZero = "RESERVE_BELOW_ZERO";

    [ErrorCode(422, "error.claims.above_approved_amount")]
    public const string AboveApprovedAmount = "ABOVE_APPROVED_AMOUNT";

    [ErrorCode(422, "error.claims.code_not_in_force")]
    public const string CodeNotInForce = "CLAIMS_CODE_NOT_IN_FORCE";

    [ErrorCode(409, "error.claims.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "APPROVAL_SUBJECT_CHANGED";

    [ErrorCode(409, "error.claims.bank_account_changed")]
    public const string BankAccountChanged = "PAYEE_BANK_ACCOUNT_CHANGED";

    [ErrorCode(409, "error.claims.retry_pending")]
    public const string RetryPending = "PAYMENT_RETRY_PENDING";

    [ErrorCode(404, "error.claims.claim_not_registered")]
    public const string ClaimNotRegistered = "CLAIM_NOT_REGISTERED";
}
