/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Api
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
using System.Text.RegularExpressions;
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Infrastructure.StaticData;

namespace Insys.BuildingBlocks.Api.StaticData;

/// <summary>
/// Static data functions (ADR-026, sign-off decision 4): view, maintain and approve per catalogue module, under the
/// Configuration domain with one sub-domain per INSYS module. Staff maintain their tenant's rows and platform
/// administrators the global ones, through the same functions (decision 1); the tenant and static data scopes separate the data.
/// </summary>
public static partial class StaticDataFunctions
{
    public const string Domain = "CONFIGURATION";

    /// <summary>The catalogue module's code: its name without the letter prefix, upper case, words joined by underscores.</summary>
    public static string ModuleCode(StaticDataDescriptor d) =>
        string.Join('_', NotWord().Split(LetterPrefix().Replace(d.CatalogueModule, "").ToUpperInvariant()).Where(w => w.Length > 0));

    public static string View(StaticDataDescriptor d) => $"SD_{ModuleCode(d)}_VIEW";

    public static string Maintain(StaticDataDescriptor d) => $"SD_{ModuleCode(d)}_MAINTAIN";

    public static string Approve(StaticDataDescriptor d) => $"SD_{ModuleCode(d)}_APPROVE";

    public static string SubDomain(StaticDataDescriptor d) => "CONFIG_" + string.Join('_', NotWord().Split(d.ModuleRoute.ToUpperInvariant()).Where(w => w.Length > 0));

    /// <summary>The three functions of a table's catalogue module; declaring them again for another table of the module changes nothing.</summary>
    public static void Declare(StaticDataDescriptor d)
    {
        var area = LetterPrefix().Replace(d.CatalogueModule, "");
        var feature = d.ModuleRoute switch
        {
            "life" => Features.Life,
            "general" => Features.General,
            "health" => Features.Health,
            "reinsurance" => Features.Reinsurance,
            _ => null,
        };
        var map = new FunctionMap(Audiences.StaffAndPlatform, feature)
            .Screen(View(d), SubDomain(d), area, $"configuration/{d.ModuleRoute}/{ModuleCode(d).ToLowerInvariant().Replace('_', '-')}")
            .Action(Maintain(d), View(d), Array.Empty<string>())
            // Approve and reject are one Workflow route for every table, which finds this function from the route (ADR-009).
            .Permission(Approve(d), View(d), StaticDataPermissions.Approve);
        FunctionRegistry.Declare(map.Functions);
    }

    [GeneratedRegex("^[A-Z] · ")]
    private static partial Regex LetterPrefix();

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex NotWord();
}
