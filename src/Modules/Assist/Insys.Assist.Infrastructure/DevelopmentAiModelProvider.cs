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
using System.Text.Json;
using System.Text.Json.Nodes;
using Insys.Assist.Application;
using Insys.BuildingBlocks.Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Insys.Assist.Infrastructure;

/// <summary>
/// The stand-in model for a stack with no model (<c>Assist:Provider = development</c>), in Development and Testing only, so the front end's live runs prove
/// AI-1 and AI-2 end to end (BR-147, widened to AI-1). It is deterministic and calls nothing:
/// <list type="bullet">
/// <item>AI-2: a fixed explanation made of the facts sent, so every number in it is in the facts and the verifier passes.</item>
/// <item>AI-1: a fixed extraction for the published test document (<c>docs/frontend/ai1-sample-product.pdf</c>, <see cref="SampleMarker"/>): a Found value quoted
/// at 12 characters or more, an Interpreted one, a cited proposal and the rate table, leaving the coverages Missing. Any other document gets no proposals.</item>
/// <item>AI-3 (7.8): each tailorable row tailored from its first answer, and on the published test document (<c>docs/frontend/ai3-sample-tenant-documents.pdf</c>,
/// <see cref="SetupSampleMarker"/>) fixed mappings, one with a wrong figure that INSYS's check drops, and one title it couldn't map.</item>
/// </list>
/// It can't exist anywhere else: its constructor throws, and <see cref="DevelopmentModelGuard"/> stops the host starting.
/// </summary>
public sealed class DevelopmentAiModelProvider : IAiModelProvider
{
    public const string Provider = "development";

    /// <summary>The environments it is allowed in, as for the development scanner and signing key.</summary>
    public static readonly IReadOnlyList<string> AllowedEnvironments = ["Development", "Testing"];

    /// <summary>The line on the test document's first page that the stand-in recognises it by.</summary>
    public const string SampleMarker = "INSYS AI-1 SAMPLE PRODUCT DOCUMENT";

    public const string ServedModel = "development-stand-in";

    /// <summary>The line on AI-3's test document's first page (<c>docs/frontend/ai3-sample-tenant-documents.pdf</c>) that the stand-in recognises it by.</summary>
    public const string SetupSampleMarker = "INSYS AI-3 SAMPLE TENANT DOCUMENT";

    public DevelopmentAiModelProvider(IHostEnvironment environment) => DevelopmentModelGuard.Refuse(environment);

    public Task<AiModelResponse> Complete(AiModelRequest request, CancellationToken cancellationToken)
    {
        var data = Unwrap(request.Data);
        var text = TrySetup(data) ?? TryDraft(data) ?? "Development stand-in, not a model's answer. The facts INSYS recorded:\n" + data;
        return Task.FromResult(new AiModelResponse(text, 0, 0, ServedModel, request.InferenceGeo));
    }

    /// <summary>AI-1's fixed extraction, when the data is AI-1's and holds the test document; null for AI-2's facts.</summary>
    private static string? TryDraft(string data)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?["documents"] is not JsonArray documents || payload["fields"] is null)
        {
            return null;
        }

        var sample = documents.OfType<JsonObject>().FirstOrDefault(d => d["pages"] is JsonArray pages
            && pages.Any(p => p?["text"]?.GetValue<string>().Contains(SampleMarker, StringComparison.Ordinal) == true));
        if (sample is null)
        {
            return """{"values": [], "rows": [], "rateTables": [], "rules": [], "simulationCases": []}""";
        }

        var number = sample["document"]!.GetValue<int>();
        object Cite(int page, string quote) => new { document = number, page, quote };
        // The rate table exactly as INSYS extracted it from page 2, so it passes the cell-for-cell check.
        var table = (sample["tables"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["page"]?.GetValue<int>() == 2);
        var rateTables = table is null ? [] : new object[] { new { citation = Cite(2, "Monthly premium by age band"), rows = table["rows"] } };
        return JsonSerializer.Serialize(new
        {
            values = new object[]
            {
                new { field = "version.name", value = "Sample Funeral Plan", flag = "FOUND", citation = Cite(1, "Product name: Sample Funeral Plan") },
                new { field = "version.coverStartsOn", value = "FirstPayment", flag = "INTERPRETED", citation = Cite(1, "Cover starts when the first premium is paid") },
            },
            rows = new object[] { new { field = "rating.cfg_premium_bounds", value = new { currency = "ZAR", minimum = "45.00" }, flag = "INTERPRETED",
                citation = Cite(1, "The monthly premium is at least R45.00") } },
            rateTables,
            rules = new object[] { new { text = "Cover ends at age 75", citation = Cite(1, "Cover ends at age 75") } },
            simulationCases = new object[] { new { description = "Youngest entry age", inputs = new { age = 18 } } },
        });
    }

    /// <summary>
    /// AI-3's fixed answer (7.8), when the data is AI-3's: each tailorable row's first column from its first answer's first value; on the test document, two
    /// accounts, one approval limit, a role title mapped onto a template, one approval limit with a wrong figure (which INSYS's check drops) and one title it
    /// couldn't map. Any other document gets no mappings.
    /// </summary>
    private static string? TrySetup(string data)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?["targets"] is not JsonArray targets || payload["tailorable"] is not JsonArray rows || payload["documents"] is not JsonArray documents)
        {
            return null;
        }

        var tailored = rows.OfType<JsonObject>()
            .Select(r => (Row: r, Column: (r["columns"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
                Answer: (r["answers"] as JsonObject)?.FirstOrDefault(a => a.Value is JsonArray { Count: > 0 })))
            .Where(x => x.Column is not null && x.Answer is { Key: not null } a && a.Value is JsonArray)
            .Select(x => (object)new { table = x.Row["table"]!.GetValue<string>(), code = x.Row["code"]!.GetValue<string>(), column = x.Column,
                answer = x.Answer!.Value.Key, value = (x.Answer.Value.Value as JsonArray)![0]!.GetValue<string>() })
            .ToArray();

        var sample = documents.OfType<JsonObject>().FirstOrDefault(d => d["pages"] is JsonArray pages
            && pages.Any(p => p?["text"]?.GetValue<string>().Contains(SetupSampleMarker, StringComparison.Ordinal) == true));
        if (sample is null)
        {
            return JsonSerializer.Serialize(new { tailored, mapped = Array.Empty<object>(), unmapped = Array.Empty<object>() });
        }

        var number = sample["document"]!.GetValue<int>();
        object Cite(int page, string quote) => new { document = number, page, quote };
        var roles = targets.OfType<JsonObject>().FirstOrDefault(t => t["table"]?.GetValue<string>() == SetupMappingTargets.Roles)?["columns"] as JsonArray;
        var templates = (roles?.OfType<JsonObject>().FirstOrDefault(c => c["column"]?.GetValue<string>() == "role_code")?["allowed"] as JsonArray)?
            .Select(a => a!.GetValue<string>()).ToList() ?? [];
        var template = templates.FirstOrDefault(t => t.Contains("CLAIM", StringComparison.Ordinal)) ?? templates.FirstOrDefault();
        var mapped = new List<object>
        {
            new { table = SetupMappingTargets.ChartOfAccounts, value = new { account_code = "4100", name = "Premium income", type = "INCOME", gl_account = "GL 4100", status = "ACTIVE" },
                citation = Cite(1, "4100 Premium income INCOME GL 4100") },
            new { table = SetupMappingTargets.ChartOfAccounts, value = new { account_code = "2100", name = "Claims payable", type = "LIABILITY", gl_account = "GL 2100", status = "ACTIVE" },
                citation = Cite(1, "2100 Claims payable LIABILITY GL 2100") },
            new { table = SetupMappingTargets.ApprovalAuthority,
                value = new { role_code = "CLAIMS_MANAGER", transaction_type = "CLAIM_PAYMENT", limit_value = "250000", currency = "ZAR", delegation_allowed = false },
                citation = Cite(2, "Claims managers approve claim payments up to ZAR 250,000.") },
            // A wrong figure: the quote says 50,000, so INSYS's check drops it (AIA-087).
            new { table = SetupMappingTargets.ApprovalAuthority,
                value = new { role_code = "FINANCE_MANAGER", transaction_type = "PREMIUM_REFUND", limit_value = "500000", currency = "ZAR", delegation_allowed = false },
                citation = Cite(2, "Finance managers approve premium refunds up to ZAR 50,000.") },
        };
        if (template is not null)
        {
            mapped.Add(new { table = SetupMappingTargets.Roles, value = new { role_code = template, title = "Claims Officer" }, citation = Cite(3, "Role list Claims Officer") });
        }

        return JsonSerializer.Serialize(new
        {
            tailored, mapped,
            unmapped = new object[] { new { document = number, page = 3, quote = "Compliance Officer", table = SetupMappingTargets.Roles } },
        });
    }

    private static string Unwrap(string data)
    {
        var text = data.Trim();
        if (text.StartsWith(PromptData.Open, StringComparison.Ordinal))
        {
            text = text[PromptData.Open.Length..];
        }

        if (text.EndsWith(PromptData.Close, StringComparison.Ordinal))
        {
            text = text[..^PromptData.Close.Length];
        }

        return text.Trim();
    }
}

/// <summary>Stops a host whose settings choose the stand-in model outside Development and Testing; registered before the jobs that call a model.</summary>
public sealed class DevelopmentModelGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(configuration["Assist:Provider"], DevelopmentAiModelProvider.Provider, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(environment);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static void Refuse(IHostEnvironment environment)
    {
        if (!DevelopmentAiModelProvider.AllowedEnvironments.Contains(environment.EnvironmentName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Assist:Provider is 'development' in the {environment.EnvironmentName} environment. The stand-in model answers with fixed text and calls no model, "
                + $"so it is allowed only in {string.Join(" and ", DevelopmentAiModelProvider.AllowedEnvironments)}; set Assist:Provider to none or claude.");
        }
    }
}
