/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using System.Net.Http.Json;
using System.Text.Json;
using Insys.Assist.Evaluation;
using Insys.BuildingBlocks.Application;
using Insys.Rating.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// The AI-2 evaluation set (ai-assist.md §6 and §7, batch 7.3), drawn from the sample tenant just seeded: every refusal the error catalogue words, and the
/// sample products' premiums over a fixed grid of inputs, rated by Rating itself. The committed file must match, so the set follows the catalogue and the
/// products. To accept a change, run with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
internal static class AssistEvaluationSet
{
    public static string FilePath => Path.Combine(RepositoryPaths.Root, "docs", "evaluation", "ai2-evaluation-set.json");

    /// <summary>Each sample product, its line, the input its one table reads, the values of that input, and the sums insured.</summary>
    private static readonly (string Product, string Domain, string Attribute, string[] Keys, int[] Sums)[] Grid =
    [
        ("OFT-MOTOR", "GENERAL", "vehicle_group", ["G3"], [25_000, 50_000, 75_000, 100_000, 150_000, 200_000, 250_000, 350_000]),
        ("OFT-HOME", "GENERAL", "construction", ["BRICK", "THATCH"], [250_000, 500_000, 1_000_000, 1_500_000, 2_000_000]),
        ("OFT-FUNERAL", "LIFE", "age_band", ["18-40", "41-65"], [10_000, 20_000, 30_000, 50_000]),
        ("OFT-TERM", "LIFE", "age_band", ["18-40", "41-65"], [250_000, 500_000, 1_000_000]),
        ("OFT-HEALTH", "HEALTH", "plan_option", ["ESS", "PLUS"], [50_000, 100_000]),
    ];

    /// <summary>
    /// The adjustment schedules each product's premiums are also rated with (an input, `adjustments`): OFT-HOME's sample discount and loading, alone and
    /// together (Ade, 17 November). The other products have none, so they are rated once.
    /// </summary>
    private static string[][] AdjustmentsFor(string product) => product == "OFT-HOME" ? [[], ["OFT-HOM-NCD"], ["OFT-HOM-FLOOD"], ["OFT-HOM-NCD", "OFT-HOM-FLOOD"]] : [[]];

    public static async Task Check(InsysApiFactory host, Guid tenant, string productManager)
    {
        var refusals = host.Services.GetRequiredService<IErrorCatalogue>().All.Where(e => e.Means is { Length: > 0 })
            .Select((e, i) => new RefusalCase($"R-{i + 1:000}", e.Code, e.Retryable, e.Means!, e.Remedy, e.Code == "VALIDATION_FAILED" ? 2 : 0)).ToList();

        var client = host.StrictClientFor(tenant, productManager);
        var calculations = new List<CalculationCase>();
        foreach (var (product, domain, attribute, keys, sums) in Grid)
        {
            foreach (var key in keys)
            {
                foreach (var (sum, adjustments) in sums.SelectMany(sum => AdjustmentsFor(product).Select(a => (sum, a))))
                {
                    var values = new Dictionary<string, object> { ["currency"] = "ZAR", ["frequency"] = "MONTHLY", ["sum_insured"] = sum, [attribute] = key };
                    if (adjustments.Length > 0)
                    {
                        values["adjustments"] = adjustments;
                    }

                    var answer = await client.PostAsJsonAsync("/api/v1/rating/calculations/simulate", new { productCode = product, productVersion = 1, currency = "ZAR", frequency = "MONTHLY", values });
                    answer.IsSuccessStatusCode.ShouldBeTrue($"{product} {key} {sum}: {await answer.Content.ReadAsStringAsync()}");
                    var view = (await answer.Content.ReadFromJsonAsync<JsonElement>()).Clone();
                    var lines = view.GetProperty("lines").EnumerateArray().Select(l => new RecordedCalculationLine(l.GetProperty("lineNo").GetInt32(), l.GetProperty("component").GetString()!,
                        Text(l, "factorCode"), Text(l, "factorValue"), l.GetProperty("amount").GetDecimal())).ToList();
                    calculations.Add(new CalculationCase($"C-{calculations.Count + 1:000}", domain, product, 1, view.GetProperty("ratingVersion").GetInt32(), view.GetProperty("premium").GetDecimal(),
                        view.GetProperty("currency").GetString()!, lines));
                }
            }
        }

        var expected = new EvaluationSet("AI-2 evaluation set (ai-assist.md §6): the error catalogue's worded refusals and the sample tenant's product premiums. Sample data only; the rates are invented.",
            refusals, calculations).Write();
        if (Environment.GetEnvironmentVariable("INSYS_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, expected);
        }

        File.Exists(FilePath).ShouldBeTrue("Run the seed's tests once with INSYS_UPDATE_SNAPSHOTS=1 to generate the evaluation set.");
        (await File.ReadAllTextAsync(FilePath)).ShouldBe(expected,
            "The catalogue's wording or the sample products changed. Run with INSYS_UPDATE_SNAPSHOTS=1 and commit docs/evaluation/ai2-evaluation-set.json.");
        (refusals.Count + calculations.Count).ShouldBeGreaterThanOrEqualTo(100, "about 100 cases (ai-assist.md §7)");
    }

    private static string? Text(JsonElement line, string name) => line.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
