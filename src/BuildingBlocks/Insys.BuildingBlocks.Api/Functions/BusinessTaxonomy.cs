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
using Insys.BuildingBlocks.Api.StaticData;

namespace Insys.BuildingBlocks.Api.Functions;

public sealed record BusinessDomain(string Code, string Label, string Icon, int Order);

public sealed record BusinessSubDomain(string Code, string DomainCode, string Label, string Icon, int Order);

/// <summary>
/// The menu's domains and sub-domains (OQ-092): the 12 domains and 54 sub-domains of docs/specs/INSYS_NAVIGATION.md, in
/// its order, with its icons; PORTALS for the portal screens (sign-off decision 3); CONFIGURATION for static data, one
/// sub-domain per INSYS module. Labels are English; other languages come through the labels column.
/// </summary>
public static class BusinessTaxonomy
{
    public static readonly IReadOnlyList<BusinessDomain> Domains =
    [
        new("LIFE", "Life insurance", "heart", 1),
        new("GENERAL", "General insurance", "shield", 2),
        new("HEALTH", "Health insurance", "stethoscope", 3),
        new("PRODUCTS", "Products & pricing", "package", 4),
        new("CUSTOMERS", "Customers", "users", 5),
        new("DISTRIBUTION", "Distribution", "briefcase", 6),
        new("CLAIMS", "Claims framework", "list-ordered", 7),
        new("MONEY", "Money", "wallet", 8),
        new("COMPLIANCE", "Compliance & risk", "file-check", 9),
        new("SERVICE", "Service & communications", "headset", 10),
        new("OPERATIONS", "Operations", "workflow", 11),
        new("PLATFORM", "Platform", "building-2", 12),
        new("PORTALS", "Portals", "app-window", 13),
        new("CONFIGURATION", "Configuration", "settings", 14),
    ];

    private static readonly IReadOnlyList<BusinessSubDomain> Fixed =
    [
        new("LIFE_NEW_BUSINESS", "LIFE", "New Business", "file-plus", 1),
        new("LIFE_UNDERWRITING", "LIFE", "Underwriting", "stethoscope", 2),
        new("LIFE_POLICY_ADMIN", "LIFE", "Policy Administration", "shield-check", 3),
        new("LIFE_POLICY_VALUES", "LIFE", "Policy Values", "piggy-bank", 4),
        new("LIFE_CLAIMS", "LIFE", "Life Claims", "heart-pulse", 5),
        new("LIFE_GROUP_SCHEMES", "LIFE", "Group Schemes", "users", 6),
        new("GENERAL_RISK_OBJECTS", "GENERAL", "Risk Objects", "car", 7),
        new("GENERAL_QUOTATION", "GENERAL", "Quotation", "calculator", 8),
        new("GENERAL_POLICY_ADMIN", "GENERAL", "Policy Administration", "shield", 9),
        new("GENERAL_RENEWALS", "GENERAL", "Renewals", "refresh-cw", 10),
        new("GENERAL_CLAIMS", "GENERAL", "General Claims", "clipboard-list", 11),
        new("GENERAL_RECOVERIES", "GENERAL", "Recoveries", "undo-2", 12),
        new("HEALTH_SCHEMES_PLANS", "HEALTH", "Schemes & Plans", "layers", 13),
        new("HEALTH_MEMBERSHIP", "HEALTH", "Membership", "id-card", 14),
        new("HEALTH_ACCESS_TO_CARE", "HEALTH", "Access to Care", "badge-check", 15),
        new("HEALTH_MEDICAL_CLAIMS", "HEALTH", "Medical Claims", "file-heart", 16),
        new("HEALTH_FRAUD_UTILISATION", "HEALTH", "Fraud & Utilisation", "scan-search", 17),
        new("HEALTH_PROVIDER_PAYMENTS", "HEALTH", "Provider Payments", "banknote", 18),
        new("HEALTH_PROVIDER_MANAGEMENT", "HEALTH", "Provider Management", "hospital", 19),
        new("HEALTH_PHARMACY", "HEALTH", "Pharmacy & Medication", "pill", 20),
        new("HEALTH_CARE_PROGRAMMES", "HEALTH", "Care Programmes", "activity", 21),
        new("PRODUCT_FACTORY", "PRODUCTS", "Product Factory", "package", 22),
        new("RATING", "PRODUCTS", "Rating", "sigma", 23),
        new("RULES", "PRODUCTS", "Rules", "git-branch", 24),
        new("PARTIES", "CUSTOMERS", "Parties", "contact", 25),
        new("CONSENT_PREFERENCES", "CUSTOMERS", "Consent & Preferences", "lock-keyhole", 26),
        new("DUE_DILIGENCE", "CUSTOMERS", "Due Diligence", "user-search", 27),
        new("INTERMEDIARIES", "DISTRIBUTION", "Intermediaries", "briefcase", 28),
        new("COMMISSION", "DISTRIBUTION", "Commission", "percent", 29),
        new("CHANNELS", "DISTRIBUTION", "Channels", "waypoints", 30),
        new("CLAIM_REGISTER", "CLAIMS", "Claim Register", "list-ordered", 31),
        new("CLAIM_FINANCIALS", "CLAIMS", "Claim Financials", "scale", 32),
        new("FRAUD_SCREENING", "CLAIMS", "Fraud Screening", "shield-alert", 33),
        new("DISPUTES_APPEALS", "CLAIMS", "Disputes & Appeals", "gavel", 34),
        new("BILLING", "MONEY", "Billing", "receipt", 35),
        new("PAYMENTS", "MONEY", "Payments", "credit-card", 36),
        new("FINANCE", "MONEY", "Finance", "landmark", 37),
        new("REINSURANCE", "MONEY", "Reinsurance", "network", 38),
        new("REGULATORY_REPORTING", "COMPLIANCE", "Regulatory Reporting", "file-check", 39),
        new("RISK_LIMITS", "COMPLIANCE", "Risk Limits", "gauge", 40),
        new("DATA_PROTECTION", "COMPLIANCE", "Data Protection", "fingerprint", 41),
        new("DISCLOSURES_FRAUD", "COMPLIANCE", "Disclosures & Fraud Investigation", "search-check", 42),
        new("SOLVENCY", "COMPLIANCE", "Solvency", "bar-chart-3", 43),
        new("CUSTOMER_SERVICE", "SERVICE", "Customer Service", "headset", 44),
        new("NOTIFICATIONS", "SERVICE", "Notifications", "bell-ring", 45),
        new("DOCUMENTS", "OPERATIONS", "Documents", "folder-open", 46),
        new("WORKFLOW", "OPERATIONS", "Workflow", "workflow", 47),
        new("ANALYTICS", "OPERATIONS", "Analytics", "line-chart", 48),
        new("INTEGRATION_OPERATIONS", "OPERATIONS", "Integration Operations", "plug", 49),
        new("TENANTS", "PLATFORM", "Tenants", "building-2", 50),
        new("IDENTITY_ACCESS", "PLATFORM", "Identity & Access", "key-round", 51),
        new("STATIC_DATA", "PLATFORM", "Static Data", "database", 52),
        new("AUDIT", "PLATFORM", "Audit", "history", 53),
        new("LOCALISATION", "PLATFORM", "Localisation", "languages", 54),
        new("CUSTOMER_PORTAL", "PORTALS", "Customer portal", "user", 55),
        new("BROKER_PORTAL", "PORTALS", "Broker portal", "briefcase", 56),
        new("PROVIDER_PORTAL", "PORTALS", "Provider portal", "hospital", 57),
    ];

    /// <summary>Every sub-domain: the fixed ones, then the Configuration sub-domains the functions use, one per INSYS module with static data.</summary>
    public static IReadOnlyList<BusinessSubDomain> SubDomains(IEnumerable<string> usedSubDomains) =>
    [
        .. Fixed,
        .. usedSubDomains.Where(c => c.StartsWith("CONFIG_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select((code, i) => new BusinessSubDomain(code, StaticDataFunctions.Domain, Title(code["CONFIG_".Length..]), "settings", 100 + i)),
    ];

    private static string Title(string code) =>
        string.Join(' ', code.Split('_').Select((w, i) => i == 0 ? w[..1] + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));
}
