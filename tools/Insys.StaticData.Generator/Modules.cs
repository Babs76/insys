/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : StaticData.Generator
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
namespace Insys.StaticData.Generator;

/// <summary>The owning module of each schema (db/roles/module_roles.csv; ADR-002).</summary>
internal sealed record ModuleInfo(string Name, string Route, string PrimarySchema);

internal static class Modules
{
    public static readonly IReadOnlyDictionary<string, ModuleInfo> BySchema = new Dictionary<string, ModuleInfo>
    {
        ["integration"] = new("Integration", "integration", "integration"),
        ["channel"] = new("Integration", "integration", "integration"),
        ["sdm"] = new("StaticData", "static-data", "sdm"),
        ["refdata"] = new("StaticData", "static-data", "sdm"),
        ["ops"] = new("StaticData", "static-data", "sdm"),
        ["party"] = new("Party", "party", "party"),
        ["product"] = new("ProductFactory", "product-factory", "product"),
        ["rating"] = new("Rating", "rating", "rating"),
        ["rules"] = new("Rules", "rules", "rules"),
        ["workflow"] = new("Workflow", "workflow", "workflow"),
        ["document"] = new("Documents", "documents", "document"),
        ["billing"] = new("Billing", "billing", "billing"),
        ["payment"] = new("Payments", "payments", "payment"),
        ["claims"] = new("ClaimsFramework", "claims-framework", "claims"),
        ["life"] = new("Life", "life", "life"),
        ["general"] = new("General", "general", "general"),
        ["health"] = new("Health", "health", "health"),
        ["finance"] = new("Finance", "finance", "finance"),
        ["commission"] = new("Distribution", "distribution", "commission"),
        ["reinsurance"] = new("Reinsurance", "reinsurance", "reinsurance"),
        ["reporting"] = new("Compliance", "compliance", "reporting"),
        ["analytics"] = new("Analytics", "analytics", "analytics"),
        ["tenant"] = new("Tenancy", "tenancy", "tenant"),
        ["iam"] = new("Identity", "identity", "iam"),
        ["audit"] = new("Audit", "audit", "audit"),
        ["communication"] = new("Communications", "communications", "communication"),
    };
}
