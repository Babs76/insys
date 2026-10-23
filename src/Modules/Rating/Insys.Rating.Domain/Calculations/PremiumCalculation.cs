/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Rating
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

namespace Insys.Rating.Domain.Calculations;

/// <summary>Entity ResultLine (rating.rating_result_line): one step of the breakdown.</summary>
public sealed class ResultLine : Entity<Guid>, ITenantScoped
{
    private ResultLine()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public short LineNo { get; private set; }

    public string Component { get; private set; } = "";

    public string? FactorCode { get; private set; }

    public string? FactorValue { get; private set; }

    /// <summary>The premium after this step.</summary>
    public decimal Amount { get; private set; }

    internal static ResultLine From(Guid requestId, BreakdownLine line) => new()
    {
        Id = Guid.CreateVersion7(),
        RequestId = requestId,
        LineNo = checked((short)line.LineNo),
        Component = line.Component,
        FactorCode = line.FactorCode,
        FactorValue = line.FactorValue,
        Amount = line.Amount,
    };
}

public sealed record PremiumCalculated(Guid CalculationId, string RequestRef, string SourceDomain, string SubjectType, Guid SubjectId, string ProductCode,
    int ProductVersion, int RatingVersion, decimal Premium, string Currency, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The PremiumCalculation aggregate (rating.rating_request): immutable once calculated, a recalculation is a new
/// record; it records the product and rating versions used (PRD-008) and its inputs, so recalculating with the same
/// inputs and versions gives the same result (LIF-002).
/// </summary>
public sealed class PremiumCalculation : AggregateRoot<Guid>
{
    private readonly List<ResultLine> _lines = [];

    private PremiumCalculation()
    {
    }

    public string RequestRef { get; private set; } = "";

    /// <summary>LIFE, GENERAL, HEALTH, or PRODUCT for a simulation.</summary>
    public string SourceDomain { get; private set; } = "";

    public string SubjectType { get; private set; } = "";

    public Guid SubjectId { get; private set; }

    public string ProductCode { get; private set; } = "";

    public int ProductVersion { get; private set; }

    public int RatingVersion { get; private set; }

    /// <summary>The rating input as JSON (currency, frequency, rating date and values).</summary>
    public string Inputs { get; private set; } = "{}";

    public decimal Premium { get; private set; }

    public string Currency { get; private set; } = "";

    public DateTimeOffset CalculatedAt { get; private set; }

    public IReadOnlyList<ResultLine> Lines => _lines;

    /// <summary>Command CalculatePremium, from a breakdown the reference engine produced.</summary>
    public static PremiumCalculation CalculatePremium(Guid id, string requestRef, string sourceDomain, string subjectType, Guid subjectId, string productCode,
        int productVersion, string inputsJson, PremiumBreakdown breakdown, DateTimeOffset now)
    {
        var calculation = new PremiumCalculation
        {
            Id = Guard.NotEmpty(id),
            RequestRef = Guard.MaxLength(Guard.NotNullOrWhiteSpace(requestRef), 30),
            SourceDomain = Guard.MaxLength(Guard.NotNullOrWhiteSpace(sourceDomain), 10),
            SubjectType = Guard.MaxLength(Guard.NotNullOrWhiteSpace(subjectType), 30),
            SubjectId = Guard.NotEmpty(subjectId),
            ProductCode = Guard.MaxLength(Guard.NotNullOrWhiteSpace(productCode), 15),
            ProductVersion = productVersion,
            RatingVersion = breakdown.RatingVersion,
            Inputs = inputsJson,
            Premium = breakdown.Premium,
            Currency = breakdown.Currency,
            CalculatedAt = now,
        };
        calculation._lines.AddRange(breakdown.Lines.Select(l => ResultLine.From(id, l)));
        calculation.Raise(new PremiumCalculated(id, calculation.RequestRef, calculation.SourceDomain, calculation.SubjectType, subjectId, productCode, productVersion,
            breakdown.RatingVersion, breakdown.Premium, breakdown.Currency, now));
        calculation.MarkChanged();
        return calculation;
    }
}
