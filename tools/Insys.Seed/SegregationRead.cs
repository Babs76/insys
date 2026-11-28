/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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

namespace Insys.Seed;

/// <summary>
/// The Preview's post-upgrade read of segregation-of-duties conflicts (Ade, 28 November 2026), read only through the API and nothing else: as the
/// platform operator it finds OFTZA (ListTenants), then, as OFTZA's first administrator, reads IdentityListSegregationConflicts. The report names
/// each conflict's rule and functions only, never a user. Exits 0 when read (empty or not: the report says which), 3 when it couldn't be read.
/// </summary>
public static class SegregationRead
{
    public const string Path = "/api/v1/identity/access-reviews/segregation-conflicts";
    public const int Read = 0, Unread = 3;

    public static async Task<int> RunAndReport(Persona platform, Func<Guid, Persona> tenantAdministrator, TextWriter output, TextWriter error)
    {
        try
        {
            var tenant = await Find(platform, OftzaSeed.TenantCode);
            if (tenant is null)
            {
                error.WriteLine($"segregation-conflicts: unread: no tenant {OftzaSeed.TenantCode}");
                return Unread;
            }

            var conflicts = await tenantAdministrator(tenant.Value).Send(HttpMethod.Get, Path);
            return Report(conflicts, output);
        }
        catch (Exception e) when (e is SeedRefusedException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            error.WriteLine($"segregation-conflicts: unread: {e.Message}");   // the API's refusal (its code and title), never a token
            return Unread;
        }
    }

    /// <summary>One line: the count, then each conflict's rule and its two functions; no user id or name.</summary>
    public static int Report(JsonElement conflicts, TextWriter output)
    {
        var items = conflicts.EnumerateArray()
            .Select(c => $"{c.GetProperty("rule").GetString()} ({c.GetProperty("functionA").GetString()} and {c.GetProperty("functionB").GetString()})")
            .Order(StringComparer.Ordinal).ToList();
        output.WriteLine(items.Count == 0
            ? $"segregation-conflicts: {OftzaSeed.TenantCode}: none"
            : $"segregation-conflicts: {OftzaSeed.TenantCode}: {items.Count}: {string.Join("; ", items)}");
        return Read;
    }

    private static async Task<Guid?> Find(Persona platform, string code)
    {
        string? cursor = null;
        do
        {
            var page = await platform.Send(HttpMethod.Get, "/api/v1/tenancy/tenants?limit=100" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            foreach (var tenant in page.GetProperty("items").EnumerateArray())
            {
                if (string.Equals(tenant.GetProperty("tenantCode").GetString(), code, StringComparison.Ordinal))
                {
                    return tenant.GetProperty("id").GetGuid();
                }
            }

            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return null;
    }
}
