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
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Rating.Infrastructure.StaticData;

namespace Insys.Rating.Infrastructure.Persistence;

/// <summary>
/// A rating test case's inputs must give the currency (ISO 4217) and the premium frequency, which every simulation run
/// rates with (PRD-005, GAP-024). Refused when the case is drafted, not discovered when a simulation run fails.
/// </summary>
internal sealed partial class RatingTestCaseValidator : IStaticRecordValidator
{
    public string Schema => "rating";

    public string Table => "cfg_rating_test_case_library";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        if (record is not RatingTestCaseLibrary testCase)
        {
            return new Dictionary<string, string[]>();
        }

        var problems = new List<string>();
        if (testCase.Inputs.ValueKind != JsonValueKind.Object)
        {
            problems.Add("inputs must be an object with currency and frequency.");
        }
        else
        {
            if (!testCase.Inputs.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String
                || !Currency().IsMatch(currency.GetString()!))
            {
                problems.Add("inputs.currency is required: a three-letter ISO 4217 code, such as ZAR.");
            }

            if (!testCase.Inputs.TryGetProperty("frequency", out var frequency) || frequency.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(frequency.GetString()))
            {
                problems.Add("inputs.frequency is required: the premium frequency the case rates, such as ANNUAL or MONTHLY.");
            }
        }

        return problems.Count == 0 ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { ["inputs"] = [.. problems] };
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();
}
