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
using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Domain;
using Insys.Rating.Contracts;
using Insys.Rating.Domain.Calculations;

namespace Insys.Rating.Application;

/// <summary>The rating configuration in force for a product version, currency and day (cached; ADR-007).</summary>
public interface IRatingConfigurationReader
{
    Task<Result<RatingConfiguration>> Read(string productCode, int productVersion, string currency, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Drops the tenant's cached configuration, for example when rating static data or a product version changes.</summary>
    void Invalidate(Guid tenantId);
}

/// <summary>A case of rating.cfg_rating_test_case_library. Its inputs carry the currency and frequency besides the rating values.</summary>
public sealed record RatingTestCase(string CaseId, JsonElement Inputs, decimal ExpectedPremium, decimal? Tolerance);

public interface IRatingTestCases
{
    Task<IReadOnlyList<RatingTestCase>> For(string productCode, int productVersion, DateOnly day, CancellationToken cancellationToken);
}

public interface IPremiumCalculationRepository
{
    void Add(PremiumCalculation calculation);

    /// <summary>Tells the module that asked for a calculation through an event why none was made (PremiumNotCalculated).</summary>
    void NotCalculated(PremiumNotCalculated failure, Guid subjectId, DateTimeOffset now);
}

public sealed record ResultLineView(int LineNo, string Component, string? FactorCode, string? FactorValue, decimal Amount);

/// <param name="SourceDomain">The domain that asked for the calculation (BR-025).</param>
/// <param name="SubjectType">What was rated: the values INSYS's own modules send are listed (BR-025).</param>
public sealed record CalculationView(Guid Id, string RequestRef,
    [property: System.ComponentModel.DataAnnotations.AllowedValues("LIFE", "GENERAL", "HEALTH", "PRODUCT")] string SourceDomain,
    [property: BuildingBlocks.Domain.KnownValues("GENERAL_QUOTE", "GENERAL_ENDORSEMENT", "LIFE_PROPOSAL")] string SubjectType, Guid SubjectId, string ProductCode, int ProductVersion,
    int RatingVersion, JsonElement Inputs, decimal Premium, string Currency, DateTimeOffset CalculatedAt, IReadOnlyList<ResultLineView> Lines);

public sealed record SimulationView(decimal Premium, string Currency, int RatingVersion, IReadOnlyList<ResultLineView> Lines);

public interface IPremiumCalculationQueries
{
    Task<CalculationView?> Get(Guid id, CancellationToken cancellationToken);

    /// <summary>A subject's calculations, newest first, those before <paramref name="beforeId"/> when it is given (BR-025).</summary>
    Task<IReadOnlyList<CalculationView>> ForSubject(string subjectType, Guid subjectId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>Command CalculatePremium: rates and records the calculation with its breakdown.</summary>
public sealed record CalculatePremium(string RequestRef, string SourceDomain, string SubjectType, Guid SubjectId, RatingRequest Rating) : ICommand<CalculationView>;

/// <summary>Rates without recording anything (a dry run).</summary>
public sealed record SimulatePremium(RatingRequest Rating) : IQuery<SimulationView>;

public sealed record RunRatingTestCases(string ProductCode, int ProductVersion, DateOnly Day) : IQuery<IReadOnlyList<RatingTestCaseResult>>;

public sealed record GetCalculation(Guid Id) : IQuery<CalculationView>;

public sealed record ListCalculations(string SubjectType, Guid SubjectId, Guid? BeforeId, int Limit) : IQuery<IReadOnlyList<CalculationView>>;

public sealed class CalculatePremiumValidator : AbstractValidator<CalculatePremium>
{
    public CalculatePremiumValidator()
    {
        RuleFor(c => c.RequestRef).NotEmpty().MaximumLength(30);
        RuleFor(c => c.SourceDomain).NotEmpty().Must(d => d is "LIFE" or "GENERAL" or "HEALTH" or "PRODUCT").WithMessage("Source domain is LIFE, GENERAL, HEALTH or PRODUCT.");
        RuleFor(c => c.SubjectType).NotEmpty().MaximumLength(30);
        RuleFor(c => c.SubjectId).NotEmpty();
        RuleFor(c => c.Rating).SetValidator(new RatingRequestValidator());
    }
}

public sealed class SimulatePremiumValidator : AbstractValidator<SimulatePremium>
{
    public SimulatePremiumValidator() => RuleFor(c => c.Rating).SetValidator(new RatingRequestValidator());
}

public sealed class RatingRequestValidator : AbstractValidator<RatingRequest>
{
    public RatingRequestValidator()
    {
        RuleFor(r => r.ProductCode).NotEmpty().MaximumLength(15);
        RuleFor(r => r.ProductVersion).GreaterThan(0);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Frequency).NotEmpty().MaximumLength(20);
        RuleFor(r => r.Values.ValueKind).Equal(JsonValueKind.Object).WithMessage("Values must be a JSON object.");
    }
}

/// <summary>Rates a request with the configuration in force on its rating date.</summary>
public sealed class PremiumRater(IRatingConfigurationReader configuration, IBusinessDate businessDate)
{
    public async Task<Result<(PremiumBreakdown Breakdown, string InputsJson)>> Rate(RatingRequest request, CancellationToken cancellationToken)
    {
        var day = request.RatingDate ?? businessDate.Today;
        var config = await configuration.Read(request.ProductCode, request.ProductVersion, request.Currency, day, cancellationToken);
        if (config.IsFailure)
        {
            return config.Error;
        }

        var breakdown = RatingEngine.Calculate(config.Value, new RatingInput(request.Currency, request.Frequency, RatingValues.Flatten(request.Values)));
        if (breakdown.IsFailure)
        {
            return breakdown.Error;
        }

        var inputs = JsonSerializer.Serialize(new { currency = request.Currency, frequency = request.Frequency, ratingDate = day, values = request.Values },
            JsonSerializerOptions.Web);
        return (breakdown.Value, inputs);
    }
}

internal sealed class CalculatePremiumHandler(PremiumRater rater, IPremiumCalculationRepository calculations, IClock clock)
    : ICommandHandler<CalculatePremium, CalculationView>
{
    public async Task<Result<CalculationView>> Handle(CalculatePremium command, CancellationToken cancellationToken)
    {
        var rated = await rater.Rate(command.Rating, cancellationToken);
        if (rated.IsFailure)
        {
            return rated.Error;
        }

        var calculation = PremiumCalculation.CalculatePremium(Guid.CreateVersion7(), command.RequestRef, command.SourceDomain, command.SubjectType, command.SubjectId,
            command.Rating.ProductCode, command.Rating.ProductVersion, rated.Value.InputsJson, rated.Value.Breakdown, clock.UtcNow);
        calculations.Add(calculation);
        return CalculationViews.From(calculation);
    }
}

/// <summary>
/// Rating's policies: a quote priced as it is made (General's GeneralQuotePricingRequested, BR-095) is rated and recorded
/// as a calculation asked for through the API would be, and General attaches it from PremiumCalculated. When it cannot be
/// rated (a missing input, no rating configuration in force), nothing is recorded and General is told why.
/// </summary>
public sealed class RatingRequestPolicies(PremiumRater rater, IPremiumCalculationRepository calculations, IClock clock)
    : IIntegrationEventHandler<General.Contracts.GeneralQuotePricingRequested>
{
    public async Task Handle(General.Contracts.GeneralQuotePricingRequested requested, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var requestRef = $"{requested.QuoteNumber}-V{requested.QuoteVersion}";
        var request = new RatingRequest(requested.ProductCode, requested.ProductVersion, requested.Currency, requested.Frequency, requested.RatingDate, requested.Values);
        var rated = await rater.Rate(request, cancellationToken);
        if (rated.IsFailure)
        {
            calculations.NotCalculated(new PremiumNotCalculated(requestRef, General.Contracts.GeneralRatingSubjects.SourceDomain, General.Contracts.GeneralRatingSubjects.Quote,
                requested.QuoteId, rated.Error.Code, rated.Error.Message), requested.QuoteId, clock.UtcNow);
            return;
        }

        calculations.Add(PremiumCalculation.CalculatePremium(Guid.CreateVersion7(), requestRef, General.Contracts.GeneralRatingSubjects.SourceDomain,
            General.Contracts.GeneralRatingSubjects.Quote, requested.QuoteId, requested.ProductCode, requested.ProductVersion, rated.Value.InputsJson, rated.Value.Breakdown,
            clock.UtcNow));
    }
}

internal sealed class SimulatePremiumHandler(PremiumRater rater) : IQueryHandler<SimulatePremium, SimulationView>
{
    public async Task<Result<SimulationView>> Handle(SimulatePremium query, CancellationToken cancellationToken)
    {
        var rated = await rater.Rate(query.Rating, cancellationToken);
        return rated.IsFailure
            ? rated.Error
            : new SimulationView(rated.Value.Breakdown.Premium, rated.Value.Breakdown.Currency, rated.Value.Breakdown.RatingVersion,
                rated.Value.Breakdown.Lines.Select(CalculationViews.From).ToList());
    }
}

/// <summary>Runs the test case library for a product version (PRD-005). A case passes when the premium is within its tolerance.</summary>
internal sealed class RunRatingTestCasesHandler(IRatingTestCases cases, PremiumRater rater) : IQueryHandler<RunRatingTestCases, IReadOnlyList<RatingTestCaseResult>>
{
    public async Task<Result<IReadOnlyList<RatingTestCaseResult>>> Handle(RunRatingTestCases query, CancellationToken cancellationToken)
    {
        var results = new List<RatingTestCaseResult>();
        foreach (var testCase in await cases.For(query.ProductCode, query.ProductVersion, query.Day, cancellationToken))
        {
            var tolerance = testCase.Tolerance ?? 0m;
            var currency = Text(testCase.Inputs, "currency");
            var frequency = Text(testCase.Inputs, "frequency");
            if (currency is null || frequency is null)
            {
                results.Add(new RatingTestCaseResult(testCase.CaseId, testCase.ExpectedPremium, tolerance, null, false, RatingErrorCodes.InputMissing));
                continue;
            }

            var rated = await rater.Rate(new RatingRequest(query.ProductCode, query.ProductVersion, currency, frequency, query.Day, testCase.Inputs), cancellationToken);
            results.Add(rated.IsFailure
                ? new RatingTestCaseResult(testCase.CaseId, testCase.ExpectedPremium, tolerance, null, false, rated.Error.Code)
                : new RatingTestCaseResult(testCase.CaseId, testCase.ExpectedPremium, tolerance, rated.Value.Breakdown.Premium,
                    Math.Abs(rated.Value.Breakdown.Premium - testCase.ExpectedPremium) <= tolerance, null));
        }

        return results;
    }

    private static string? Text(JsonElement inputs, string name) =>
        inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed class GetCalculationHandler(IPremiumCalculationQueries queries) : IQueryHandler<GetCalculation, CalculationView>
{
    public async Task<Result<CalculationView>> Handle(GetCalculation query, CancellationToken cancellationToken) =>
        await queries.Get(query.Id, cancellationToken) is { } view ? view : Error.NotFound(ErrorCodes.NotFound, "The calculation was not found.");
}

internal sealed class ListCalculationsHandler(IPremiumCalculationQueries queries) : IQueryHandler<ListCalculations, IReadOnlyList<CalculationView>>
{
    public async Task<Result<IReadOnlyList<CalculationView>>> Handle(ListCalculations query, CancellationToken cancellationToken) =>
        Result.Success(await queries.ForSubject(query.SubjectType, query.SubjectId, query.BeforeId, query.Limit, cancellationToken));
}

public static class CalculationViews
{
    public static CalculationView From(PremiumCalculation c)
    {
        using var inputs = JsonDocument.Parse(c.Inputs);
        return new CalculationView(c.Id, c.RequestRef, c.SourceDomain, c.SubjectType, c.SubjectId, c.ProductCode, c.ProductVersion, c.RatingVersion,
            inputs.RootElement.Clone(), c.Premium, c.Currency, c.CalculatedAt, c.Lines.OrderBy(l => l.LineNo).Select(From).ToList());
    }

    public static ResultLineView From(ResultLine l) => new(l.LineNo, l.Component, l.FactorCode, l.FactorValue, l.Amount);

    public static ResultLineView From(BreakdownLine l) => new(l.LineNo, l.Component, l.FactorCode, l.FactorValue, l.Amount);
}

/// <summary>Flattens a JSON object of rating inputs into dotted names: {"risk": {"driver": {"age": 29}}} gives "risk.driver.age" = 29.</summary>
public static class RatingValues
{
    public static Dictionary<string, object?> Flatten(JsonElement values)
    {
        var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (values.ValueKind == JsonValueKind.Object)
        {
            Add(flat, "", values);
        }

        return flat;
    }

    private static void Add(Dictionary<string, object?> flat, string prefix, JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = prefix + property.Name;
            var value = property.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    Add(flat, name + ".", value);
                    break;
                case JsonValueKind.Array:
                    flat[name] = value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                    break;
                case JsonValueKind.Number:
                    flat[name] = value.GetDecimal();
                    break;
                case JsonValueKind.String:
                    flat[name] = value.GetString();
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    flat[name] = value.GetBoolean();
                    break;
                default:
                    flat[name] = null;
                    break;
            }
        }
    }
}
